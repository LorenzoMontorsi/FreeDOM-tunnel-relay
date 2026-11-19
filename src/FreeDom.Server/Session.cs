using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FreeDom.Core;

namespace FreeDom.Server;

/// <summary>One connected client: its control channel and the public endpoints it owns.</summary>
sealed partial class Session
{
    private readonly TunnelServer _server;
    private readonly Conn _conn;
    private readonly ControlChannel _channel;
    private readonly string _remote;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(string Host, HttpRoute Route)> _hosts = [];
    private readonly List<(int Port, TcpListener Listener, string Tunnel)> _tcp = [];
    private readonly List<int> _udpPorts = [];
    private SessionUdp? _udp;
    private string _clientId = "";
    private int _cleanedUp;

    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    public Session(TunnelServer server, Conn conn, string remote)
    {
        _server = server;
        _conn = conn;
        _channel = new ControlChannel(conn.Stream);
        _remote = remote;
    }

    private ServerOptions Opt => _server.Options;

    public async Task RunAsync(Message hello, CancellationToken serverCt)
    {
        using var link = serverCt.Register(_cts.Cancel);
        var ct = _cts.Token;
        try
        {
            if (!Opt.CheckToken(hello.Token))
            {
                Log.Warn($"rejected client {_remote}: invalid token");
                await FailAsync("invalid token", fatal: true);
                return;
            }
            if (hello.Version != Proto.Version)
            {
                await FailAsync($"protocol mismatch (server v{Proto.Version}, client v{hello.Version})", fatal: true);
                return;
            }
            var requests = hello.Tunnels ?? [];
            if (requests.Count == 0)
            {
                await FailAsync("no tunnels requested", fatal: true);
                return;
            }

            _clientId = string.IsNullOrWhiteSpace(hello.ClientId) ? Id : hello.ClientId;
            await _server.TakeOverAsync(_clientId, this);

            var assigned = new List<TunnelInfo>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                if (string.IsNullOrWhiteSpace(req.Name)) req.Name = $"tunnel{i + 1}";
                if (!names.Add(req.Name))
                {
                    await FailAsync($"duplicate tunnel name '{req.Name}'", fatal: true);
                    return;
                }
                var (info, error, fatal) = Register(req);
                if (error is not null)
                {
                    Log.Warn($"[{Id}] client {_remote}: {error}");
                    await FailAsync(error, fatal);
                    return;
                }
                assigned.Add(info!);
            }

            var ok = new Message { Type = MsgType.HelloOk, Version = Proto.Version, Assigned = assigned };
            if (_udp is not null)
            {
                ok.UdpPort = Opt.ControlPort;
                ok.UdpSession = _udp.Id;
                ok.UdpKey = Convert.ToBase64String(_udp.Key);
            }
            await _channel.SendAsync(ok, ct);
            _ready.TrySetResult();
            _udp?.Start(ct);
            Log.Info($"[{Id}] client {_remote} online: {string.Join(", ", assigned.Select(a => $"{a.Name}={a.PublicUrl}"))}");

            foreach (var (_, listener, tunnel) in _tcp)
                _ = TunnelServer.AcceptLoop(listener, s => _server.ForwardTcpAsync(this, tunnel, s, ct), ct);

            _ = HeartbeatAsync(ct);
            await ReadLoopAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"[{Id}] control channel error: {Net.Describe(ex)}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            Cleanup();
        }
    }

    private (TunnelInfo? Info, string? Error, bool Fatal) Register(TunnelRequest req)
    {
        switch (req.Type.ToLowerInvariant())
        {
            case "http":
                return RegisterHttp(req);
            case "tcp":
                return RegisterTcp(req);
            case "udp":
                return RegisterUdp(req);
            default:
                return (null, $"unknown tunnel type '{req.Type}'", true);
        }
    }

    private (TunnelInfo?, string?, bool) RegisterHttp(TunnelRequest req)
    {
        string host;
        var route = new HttpRoute(this, req.Name);

        if (!string.IsNullOrWhiteSpace(req.Hostname))
        {
            host = ServerOptions.Normalize(req.Hostname)!;
            if (!HostnameRegex().IsMatch(host)) return (null, $"invalid hostname '{req.Hostname}'", true);
            if (!_server.TryAddHost(host, route)) return (null, $"hostname {host} is already in use", false);
        }
        else
        {
            if (Opt.Domain is null)
                return (null, "server has no --domain configured: request an explicit hostname", true);

            if (!string.IsNullOrWhiteSpace(req.Subdomain))
            {
                string sub = req.Subdomain.Trim().ToLowerInvariant();
                if (!LabelRegex().IsMatch(sub)) return (null, $"invalid subdomain '{req.Subdomain}'", true);
                host = $"{sub}.{Opt.Domain}";
                if (!_server.TryAddHost(host, route)) return (null, $"subdomain {host} is already in use", false);
            }
            else
            {
                do host = $"{RandomLabel()}.{Opt.Domain}";
                while (!_server.TryAddHost(host, route));
            }
        }

        _hosts.Add((host, route));
        string port = Opt.PublicPort is { } p ? $":{p}" : "";
        return (new TunnelInfo { Name = req.Name, Type = "http", PublicUrl = $"{Opt.PublicScheme}://{host}{port}" }, null, false);
    }

    private (TunnelInfo?, string?, bool) RegisterTcp(TunnelRequest req)
    {
        int port = req.RemotePort;
        if (port != 0 && !Opt.TcpPorts.Contains(port))
            return (null, $"TCP port {port} is not allowed by the server (allowed: {Opt.TcpPorts})", true);
        if (port != 0 && _server.IsReservedPort(port))
            return (null, $"port {port} is reserved by the server", true);

        TcpListener? listener = null;
        if (port != 0)
        {
            if (!_server.TryAddPort(port, this)) return (null, $"port {port} is already in use by another tunnel", false);
            try { listener = Net.Listen(Opt.TcpBind, port); }
            catch (SocketException ex)
            {
                _server.RemovePort(port, this);
                return (null, $"cannot listen on port {port}: {ex.Message}", false);
            }
        }
        else
        {
            for (int attempt = 0; attempt < 200 && listener is null; attempt++)
            {
                int candidate = Opt.TcpPorts.Random();
                if (_server.IsReservedPort(candidate) || !_server.TryAddPort(candidate, this)) continue;
                try { listener = Net.Listen(Opt.TcpBind, candidate); port = candidate; }
                catch (SocketException) { _server.RemovePort(candidate, this); }
            }
            if (listener is null) return (null, "no free port available for TCP tunnel", false);
        }

        _tcp.Add((port, listener, req.Name));
        string host = Opt.PublicHost ?? "your-server";
        return (new TunnelInfo { Name = req.Name, Type = "tcp", PublicUrl = $"tcp://{host}:{port}" }, null, false);
    }

    private (TunnelInfo?, string?, bool) RegisterUdp(TunnelRequest req)
    {
        if (_server.Relay is null) return (null, "UDP tunnels are disabled on this server", true);

        int port = req.RemotePort;
        if (port != 0 && !Opt.UdpPorts.Contains(port))
            return (null, $"UDP port {port} is not allowed by the server (allowed: {Opt.UdpPorts})", true);
        if (port != 0 && _server.IsReservedUdpPort(port))
            return (null, $"UDP port {port} is reserved by the server", true);

        Socket? socket = null;
        if (port != 0)
        {
            if (!_server.TryAddUdpPort(port, this)) return (null, $"UDP port {port} is already in use by another tunnel", false);
            try { socket = UdpSockets.Bind(Opt.UdpBind, port); }
            catch (SocketException ex)
            {
                _server.RemoveUdpPort(port, this);
                return (null, $"cannot listen on UDP port {port}: {ex.Message}", false);
            }
        }
        else
        {
            for (int attempt = 0; attempt < 200 && socket is null; attempt++)
            {
                int candidate = Opt.UdpPorts.Random();
                if (_server.IsReservedUdpPort(candidate) || !_server.TryAddUdpPort(candidate, this)) continue;
                try { socket = UdpSockets.Bind(Opt.UdpBind, candidate); port = candidate; }
                catch (SocketException) { _server.RemoveUdpPort(candidate, this); }
            }
            if (socket is null) return (null, "no free port available for UDP tunnel", false);
        }

        _udpPorts.Add(port);
        _udp ??= new SessionUdp(_server.Relay, Id);
        ushort id = _udp.AddPort(socket, port, req.Name);
        string host = Opt.PublicHost ?? "your-server";
        return (new TunnelInfo { Id = id, Name = req.Name, Type = "udp", PublicUrl = $"udp://{host}:{port}" }, null, false);
    }

    /// <summary>Asks the client to open a data connection for <paramref name="tunnel"/> and waits for it.</summary>
    public async Task<Conn> OpenStreamAsync(string tunnel, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _ready.Task.WaitAsync(Proto.OpenTimeout, linked.Token);

        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var tcs = new TaskCompletionSource<Conn>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.AddPending(id, tcs);
        bool handedOff = false;
        try
        {
            await _channel.SendAsync(new Message { Type = MsgType.NewConn, ConnId = id, Tunnel = tunnel }, linked.Token);
            var conn = await tcs.Task.WaitAsync(Proto.OpenTimeout, linked.Token);
            handedOff = true;
            return conn;
        }
        finally
        {
            _server.RemovePending(id);
            // If the data connection arrived just as we gave up, don't leak it.
            if (!handedOff && !tcs.TrySetCanceled() && tcs.Task.IsCompletedSuccessfully)
                tcs.Task.Result.Dispose();
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Proto.PingInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await _channel.SendAsync(new Message { Type = MsgType.Ping }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[{Id}] heartbeat failed: {Net.Describe(ex)}");
            _cts.Cancel();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(Proto.IdleTimeout);
            Message msg;
            try
            {
                msg = await _channel.ReceiveAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Info($"[{Id}] client {_remote} heartbeat timeout");
                return;
            }

            if (msg.Type == MsgType.Ping)
                await _channel.SendAsync(new Message { Type = MsgType.Pong }, ct);
        }
    }

    private async Task FailAsync(string error, bool fatal)
    {
        try
        {
            await _channel.SendAsync(new Message { Type = MsgType.Error, Error = error, Fatal = fatal }, _cts.Token);
            await _conn.ShutdownWriteAsync();
        }
        catch { }
    }

    /// <summary>Forcefully ends this session (used when the same client reconnects).</summary>
    public async Task CloseAsync()
    {
        _cts.Cancel();
        _conn.Dispose();
        try { await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        Cleanup();
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return;
        _cts.Cancel();
        _ready.TrySetCanceled();
        foreach (var (host, route) in _hosts) _server.RemoveHost(host, route);
        foreach (var (port, listener, _) in _tcp)
        {
            try { listener.Stop(); } catch { }
            _server.RemovePort(port, this);
        }
        _udp?.Dispose();
        foreach (var port in _udpPorts) _server.RemoveUdpPort(port, this);
        if (_clientId.Length > 0) _server.RemoveClient(_clientId, this);
        _conn.Dispose();
        if (_hosts.Count + _tcp.Count + _udpPorts.Count > 0) Log.Info($"[{Id}] client {_remote} offline");
        _closed.TrySetResult();
    }

    private static string RandomLabel()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(8, 0, (span, _) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostnameRegex();
}
