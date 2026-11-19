using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FreeDom.Core;

namespace FreeDom.Server;

sealed record HttpRoute(Session Session, string Tunnel);

sealed class TunnelServer(ServerOptions options, X509Certificate2? certificate)
{
    private const int MaxHttpHead = 64 * 1024;

    private readonly ConcurrentDictionary<string, HttpRoute> _http = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Session> _tcpPorts = new();
    private readonly ConcurrentDictionary<int, Session> _udpPorts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Conn>> _pending = new();
    private readonly ConcurrentDictionary<string, Session> _byClient = new();

    public ServerOptions Options { get; } = options;

    /// <summary>Encrypted UDP channel to clients; null when UDP is disabled.</summary>
    public UdpRelay? Relay { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var loops = new List<Task>();

        var control = Net.Listen(Options.ControlBind, Options.ControlPort);
        Log.Info($"Control channel listening on {Options.ControlBind}:{Options.ControlPort} ({(certificate is null ? "plain TCP" : "TLS")})");
        loops.Add(AcceptLoop(control, s => HandleControlAsync(s, ct), ct));

        if (Options.UdpEnabled)
        {
            Relay = new UdpRelay(UdpSockets.Bind(Options.ControlBind, Options.ControlPort));
            Log.Info($"UDP relay listening on {Options.ControlBind}:{Options.ControlPort}/udp " +
                     $"(UDP tunnel ports {Options.UdpPorts})");
            loops.Add(Relay.RunAsync(ct));
        }

        if (Options.HttpPort > 0)
        {
            var http = Net.Listen(Options.HttpBind, Options.HttpPort);
            Log.Info($"HTTP front listening on {Options.HttpBind}:{Options.HttpPort}" +
                     (Options.Domain is null ? " (no --domain: only explicit hostnames)" : $" for *.{Options.Domain}"));
            loops.Add(AcceptLoop(http, s => HandleHttpAsync(s, ct), ct));
        }

        if (Options.AskPort > 0)
        {
            var ask = Net.Listen("127.0.0.1", Options.AskPort);
            Log.Info($"On-demand TLS ask endpoint on http://127.0.0.1:{Options.AskPort}/check?domain=...");
            loops.Add(AcceptLoop(ask, HandleAskAsync, ct));
        }

        await Task.WhenAll(loops);
    }

    public static async Task AcceptLoop(TcpListener listener, Func<Socket, Task> handler, CancellationToken ct)
    {
        using var stop = ct.Register(listener.Stop);
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                       || (ex is SocketException && ct.IsCancellationRequested))
            {
                break;
            }
            catch (SocketException ex)
            {
                // e.g. EMFILE: back off a little instead of spinning.
                Log.Warn($"accept failed: {ex.Message}");
                await Task.Delay(100, CancellationToken.None);
                continue;
            }

            Net.Tune(socket);
            _ = Task.Run(async () =>
            {
                try { await handler(socket); }
                catch (Exception ex) { Log.Debug($"connection handler failed: {Net.Describe(ex)}"); socket.Dispose(); }
            }, CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- control

    private async Task HandleControlAsync(Socket socket, CancellationToken ct)
    {
        string remote = socket.RemoteEndPoint?.ToString() ?? "?";
        var conn = Conn.FromSocket(socket);
        Message first;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(Proto.HandshakeTimeout);
            if (certificate is not null)
            {
                var ssl = new SslStream(conn.Stream, leaveInnerStreamOpen: false);
                conn = new Conn(socket, ssl);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                }, hs.Token);
            }
            first = await Frames.ReadAsync(conn.Stream, hs.Token);
        }
        catch (Exception ex)
        {
            Log.Debug($"handshake from {remote} failed: {Net.Describe(ex)}");
            conn.Dispose();
            return;
        }

        switch (first.Type)
        {
            case MsgType.Data:
                if (first.ConnId is { } id && _pending.TryRemove(id, out var tcs) && tcs.TrySetResult(conn))
                    return;
                conn.Dispose();
                return;

            case MsgType.Hello:
                await new Session(this, conn, remote).RunAsync(first, ct);
                return;

            default:
                conn.Dispose();
                return;
        }
    }

    // Registry operations used by sessions.
    public bool TryAddHost(string host, HttpRoute route) => _http.TryAdd(host, route);
    public void RemoveHost(string host, HttpRoute route) => _http.TryRemove(KeyValuePair.Create(host, route));
    public bool TryAddPort(int port, Session s) => _tcpPorts.TryAdd(port, s);
    public bool IsPortTaken(int port) => _tcpPorts.ContainsKey(port);
    public void RemovePort(int port, Session s) => _tcpPorts.TryRemove(KeyValuePair.Create(port, s));
    public bool TryAddUdpPort(int port, Session s) => _udpPorts.TryAdd(port, s);
    public void RemoveUdpPort(int port, Session s) => _udpPorts.TryRemove(KeyValuePair.Create(port, s));
    public bool IsReservedUdpPort(int port) => Relay is not null && port == Options.ControlPort;
    public void AddPending(string id, TaskCompletionSource<Conn> tcs) => _pending[id] = tcs;
    public void RemovePending(string id) => _pending.TryRemove(id, out _);
    public void RemoveClient(string clientId, Session s) => _byClient.TryRemove(KeyValuePair.Create(clientId, s));

    /// <summary>A reconnecting client replaces its own stale session instead of colliding with it.</summary>
    public async Task TakeOverAsync(string clientId, Session session)
    {
        if (_byClient.TryGetValue(clientId, out var old) && old != session)
        {
            Log.Info($"[{session.Id}] replaces stale session [{old.Id}] of the same client");
            await old.CloseAsync();
        }
        _byClient[clientId] = session;
    }

    public bool IsReservedPort(int port) =>
        port == Options.ControlPort || (Options.HttpPort > 0 && port == Options.HttpPort)
                                    || (Options.AskPort > 0 && port == Options.AskPort);

    // ---------------------------------------------------------------- public HTTP front

    private async Task HandleHttpAsync(Socket socket, CancellationToken ct)
    {
        var visitor = Conn.FromSocket(socket);
        byte[] buffer = new byte[MaxHttpHead];
        int length, headEnd;
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(30));
            (length, headEnd) = await ReadHeadAsync(visitor.Stream, buffer, t.Token);
        }
        catch
        {
            visitor.Dispose();
            return;
        }

        if (headEnd < 0)
        {
            if (length == buffer.Length)
                await HttpText.RespondAsync(visitor, 431, "Request Header Fields Too Large", "Request head too large.");
            else
                visitor.Dispose();
            return;
        }

        string? host = GetHost(buffer.AsSpan(0, headEnd));
        if (host is null || !_http.TryGetValue(host, out var route))
        {
            Log.Debug($"http: no tunnel for host '{host}'");
            await HttpText.RespondAsync(visitor, 404, "Not Found", $"FreeDOM: no tunnel is online for {host ?? "(no Host header)"}.");
            return;
        }

        Conn data;
        try
        {
            data = await route.Session.OpenStreamAsync(route.Tunnel, ct);
        }
        catch (Exception ex)
        {
            Log.Debug($"http: tunnel '{route.Tunnel}' for {host} did not answer: {Net.Describe(ex)}");
            await HttpText.RespondAsync(visitor, 502, "Bad Gateway", "FreeDOM: the tunnel client did not respond.");
            return;
        }

        try
        {
            // Replay the bytes we consumed while routing, then go fully transparent.
            await data.Stream.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        catch
        {
            data.Dispose();
            visitor.Dispose();
            return;
        }

        Log.Debug($"http: {socket.RemoteEndPoint} -> {host} [{route.Session.Id}/{route.Tunnel}]");
        await Bridge.RunAsync(visitor, data);
    }

    public async Task ForwardTcpAsync(Session session, string tunnel, Socket socket, CancellationToken ct)
    {
        var visitor = Conn.FromSocket(socket);
        Conn data;
        try
        {
            data = await session.OpenStreamAsync(tunnel, ct);
        }
        catch (Exception ex)
        {
            Log.Debug($"tcp: tunnel '{tunnel}' did not answer: {Net.Describe(ex)}");
            visitor.Dispose();
            return;
        }
        Log.Debug($"tcp: {socket.RemoteEndPoint} -> [{session.Id}/{tunnel}]");
        await Bridge.RunAsync(visitor, data);
    }

    // ---------------------------------------------------------------- Caddy on-demand TLS "ask"

    private async Task HandleAskAsync(Socket socket)
    {
        var conn = Conn.FromSocket(socket);
        byte[] buffer = new byte[8192];
        int headEnd;
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            (_, headEnd) = await ReadHeadAsync(conn.Stream, buffer, t.Token);
        }
        catch
        {
            conn.Dispose();
            return;
        }

        string? domain = null;
        if (headEnd > 0)
        {
            string head = Encoding.Latin1.GetString(buffer, 0, headEnd);
            string[] requestLine = head[..head.IndexOf('\r')].Split(' ');
            if (requestLine.Length >= 2 && requestLine[1].IndexOf('?') is var q and >= 0)
            {
                foreach (var pair in requestLine[1][(q + 1)..].Split('&'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0] == "domain")
                        domain = ServerOptions.Normalize(Uri.UnescapeDataString(kv[1]));
                }
            }
        }

        bool known = domain is not null && _http.ContainsKey(domain);
        Log.Debug($"ask: {domain} -> {(known ? "allow" : "deny")}");
        await HttpText.RespondAsync(conn, known ? 200 : 404, known ? "OK" : "Not Found", known ? "ok" : "unknown host");
    }

    // ---------------------------------------------------------------- HTTP head parsing

    /// <summary>Reads until the end of the HTTP head. Returns headEnd = -1 on EOF or full buffer.</summary>
    private static async Task<(int Length, int HeadEnd)> ReadHeadAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int length = 0;
        while (length < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) return (length, -1);
            int searchFrom = Math.Max(0, length - 3);
            length += n;
            int idx = buffer.AsSpan(searchFrom, length - searchFrom).IndexOf("\r\n\r\n"u8);
            if (idx >= 0) return (length, searchFrom + idx + 4);
        }
        return (length, -1);
    }

    private static string? GetHost(ReadOnlySpan<byte> head)
    {
        string text = Encoding.Latin1.GetString(head);
        foreach (var line in text.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
                continue;

            string value = line[(colon + 1)..].Trim();
            if (value.StartsWith('['))
            {
                int end = value.IndexOf(']');
                value = end > 0 ? value[1..end] : value;
            }
            else if (value.LastIndexOf(':') is var p and >= 0)
            {
                value = value[..p];
            }
            return ServerOptions.Normalize(value);
        }
        return null;
    }
}
