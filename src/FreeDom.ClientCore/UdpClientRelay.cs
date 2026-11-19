using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FreeDom.Core;

namespace FreeDom.ClientCore;

/// <summary>
/// Client end of the encrypted UDP channel. Keeps the NAT mapping towards the server open and
/// gives every remote player its own local UDP socket, so the local game server sees distinct peers.
/// </summary>
sealed class UdpClientRelay : IDisposable
{
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlowIdle = TimeSpan.FromMinutes(2);

    private readonly Socket _socket;
    private readonly UdpCodec _codec;
    private readonly Dictionary<ushort, (ResolvedTunnel Tunnel, IPEndPoint Local)> _tunnels;
    private readonly ConcurrentDictionary<uint, LocalFlow> _flows = new();
    private readonly string _server;
    private volatile bool _acked;
    private int _disposed;

    private UdpClientRelay(Socket socket, UdpCodec codec, Dictionary<ushort, (ResolvedTunnel, IPEndPoint)> tunnels, string server)
    {
        _socket = socket;
        _codec = codec;
        _tunnels = tunnels;
        _server = server;
    }

    public static async Task<UdpClientRelay> CreateAsync(string host, int port, uint session, byte[] key,
        IEnumerable<(ushort Id, ResolvedTunnel Tunnel)> tunnels, CancellationToken ct)
    {
        var map = new Dictionary<ushort, (ResolvedTunnel, IPEndPoint)>();
        foreach (var (id, tunnel) in tunnels)
            map[id] = (tunnel, new IPEndPoint(await ResolveAsync(tunnel.LocalHost, ct), tunnel.LocalPort));

        var address = await ResolveAsync(host, ct);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            UdpSockets.Tune(socket);
            socket.Connect(new IPEndPoint(address, port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new UdpClientRelay(socket, new UdpCodec(key, session, isServer: false), map, $"{address}:{port}");
    }

    /// <summary>Resolves a host, preferring IPv4 ("localhost" is pinned to 127.0.0.1, where game servers listen).</summary>
    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new IOException($"cannot resolve {host}");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = ct.Register(Dispose);
        _ = KeepaliveLoopAsync(ct);
        _ = CleanupLoopAsync(ct);

        byte[] buffer = new byte[UdpCodec.MaxDatagram + 64];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException ex) when (UdpSockets.IsTransient(ex)) { continue; }
            catch (SocketException ex)
            {
                Log.Debug($"udp relay receive failed: {ex.Message}");
                try { await Task.Delay(200, ct); } catch { break; }
                continue;
            }

            if (!_codec.TryDecode(buffer.AsSpan(0, n), out var d)) continue;
            if (d.Type == UdpCodec.KeepaliveAck)
            {
                if (!_acked)
                {
                    _acked = true;
                    Log.Info($"UDP relay active ({_server})");
                }
                continue;
            }
            if (d.Type != UdpCodec.Data || !_tunnels.TryGetValue(d.Tunnel, out var tunnel)) continue;

            if (!_flows.TryGetValue(d.Flow, out var flow))
            {
                flow = OpenFlow(d.Flow, d.Tunnel, tunnel.Tunnel, tunnel.Local, ct);
                if (flow is null) continue;
            }
            flow.Touch();
            try { UdpSockets.Send(flow.Socket, buffer.AsSpan(d.PayloadOffset, d.PayloadLength)); }
            catch (ObjectDisposedException) { }
        }
    }

    private LocalFlow? OpenFlow(uint id, ushort tunnelId, ResolvedTunnel tunnel, IPEndPoint local, CancellationToken ct)
    {
        var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            UdpSockets.Tune(socket);
            socket.Connect(local);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            Log.Warn($"[{tunnel.Name}] cannot open UDP socket to {local}: {ex.Message}");
            return null;
        }
        var flow = new LocalFlow(id, tunnelId, socket);
        _flows[id] = flow;
        Log.Debug($"[{tunnel.Name}] new UDP flow {id} -> {local}");
        _ = LocalLoopAsync(flow, ct);
        return flow;
    }

    /// <summary>Local game server -> remote player.</summary>
    private async Task LocalLoopAsync(LocalFlow flow, CancellationToken ct)
    {
        byte[] rx = new byte[UdpCodec.MaxPayload];
        byte[] tx = new byte[UdpCodec.MaxDatagram];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await flow.Socket.ReceiveAsync(rx, SocketFlags.None, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException ex) when (UdpSockets.IsTransient(ex)) { continue; } // game server restarting
            catch (SocketException) { break; }

            flow.Touch();
            try
            {
                int len = _codec.Encode(UdpCodec.Data, flow.Tunnel, flow.Id, rx.AsSpan(0, n), tx);
                UdpSockets.Send(_socket, tx.AsSpan(0, len));
            }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        byte[] packet = new byte[UdpCodec.Overhead];
        var started = DateTime.UtcNow;
        bool warned = false;
        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int n = _codec.Encode(UdpCodec.Keepalive, 0, 0, [], packet);
                    _socket.Send(packet.AsSpan(0, n));
                }
                catch (SocketException) { }

                if (!_acked && !warned && DateTime.UtcNow - started > TimeSpan.FromSeconds(10))
                {
                    warned = true;
                    Log.Warn($"No UDP reply from {_server}: is the UDP port open in the server firewall? " +
                             "UDP tunnels will not work until it is.");
                }

                // A few quick keepalives first so the channel is ready immediately, then every 10s.
                var wait = tick++ < 3 ? TimeSpan.FromMilliseconds(400) : KeepaliveInterval;
                if (!_acked && tick > 3) wait = TimeSpan.FromSeconds(2);
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var flow in _flows.Values)
                {
                    if (flow.IdleFor < FlowIdle) continue;
                    if (_flows.TryRemove(KeyValuePair.Create(flow.Id, flow))) flow.Socket.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _socket.Dispose(); } catch { }
        foreach (var flow in _flows.Values)
        {
            try { flow.Socket.Dispose(); } catch { }
        }
        _flows.Clear();
    }

    private sealed class LocalFlow(uint id, ushort tunnel, Socket socket)
    {
        private long _lastSeen = Environment.TickCount64;
        public uint Id { get; } = id;
        public ushort Tunnel { get; } = tunnel;
        public Socket Socket { get; } = socket;
        public void Touch() => Volatile.Write(ref _lastSeen, Environment.TickCount64);
        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastSeen));
    }
}
