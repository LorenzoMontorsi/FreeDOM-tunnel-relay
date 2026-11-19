using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FreeDom.Core;

namespace FreeDom.Server;

/// <summary>
/// The server end of the encrypted UDP channel to clients (UDP on the control port).
/// Clients keep a NAT mapping open with keepalives; datagrams are routed by session id.
/// </summary>
sealed class UdpRelay(Socket socket) : IDisposable
{
    private readonly ConcurrentDictionary<uint, SessionUdp> _sessions = new();

    public uint Register(SessionUdp session)
    {
        while (true)
        {
            uint id = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            if (_sessions.TryAdd(id, session)) return id;
        }
    }

    public void Unregister(uint id, SessionUdp session) => _sessions.TryRemove(KeyValuePair.Create(id, session));

    public void Send(ReadOnlySpan<byte> datagram, EndPoint to)
    {
        try { socket.SendTo(datagram, SocketFlags.None, to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[UdpCodec.MaxDatagram + 64];
        var any = UdpSockets.AnyEndPoint(socket);
        using var stop = ct.Register(socket.Dispose);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException ex) when (UdpSockets.IsTransient(ex)) { continue; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn($"udp relay receive failed: {ex.Message}");
                await Task.Delay(50, CancellationToken.None);
                continue;
            }

            var packet = buffer.AsSpan(0, r.ReceivedBytes);
            if (UdpCodec.TryPeekSession(packet, out uint id) && _sessions.TryGetValue(id, out var session))
                session.OnRelayPacket(packet, (IPEndPoint)r.RemoteEndPoint);
        }
    }

    public void Dispose() => socket.Dispose();
}

/// <summary>UDP side of one client session: its public UDP ports and the visitor flows on them.</summary>
sealed class SessionUdp : IDisposable
{
    private static readonly TimeSpan FlowIdle = TimeSpan.FromMinutes(2);

    private readonly UdpRelay _relay;
    private readonly UdpCodec _codec;
    private readonly string _tag;
    private readonly List<PublicPort> _ports = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private IPEndPoint? _client;
    private ulong _highestKeepalive;
    private uint _nextFlow;
    private int _disposed;

    public SessionUdp(UdpRelay relay, string tag)
    {
        _relay = relay;
        _tag = tag;
        Key = UdpCodec.NewKey();
        Id = relay.Register(this);
        _codec = new UdpCodec(Key, Id, isServer: true);
    }

    public uint Id { get; }
    public byte[] Key { get; }

    public ushort AddPort(Socket socket, int port, string tunnel)
    {
        var p = new PublicPort((ushort)_ports.Count, socket, port, tunnel);
        _ports.Add(p);
        return p.Id;
    }

    public void Start(CancellationToken sessionCt)
    {
        var ct = CancellationTokenSource.CreateLinkedTokenSource(sessionCt, _cts.Token).Token;
        foreach (var p in _ports) _ = PublicLoopAsync(p, ct);
        _ = CleanupLoopAsync(ct);
    }

    /// <summary>Visitor -> client: wrap each datagram and send it through the relay.</summary>
    private async Task PublicLoopAsync(PublicPort port, CancellationToken ct)
    {
        byte[] rx = new byte[UdpCodec.MaxDatagram];
        byte[] tx = new byte[UdpCodec.MaxDatagram + UdpCodec.Overhead];
        var any = UdpSockets.AnyEndPoint(port.Socket);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await port.Socket.ReceiveFromAsync(rx, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException ex) when (UdpSockets.IsTransient(ex)) { continue; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Debug($"[{_tag}] udp :{port.Port} receive failed: {ex.Message}");
                await Task.Delay(50, CancellationToken.None);
                continue;
            }

            var client = Volatile.Read(ref _client);
            if (client is null || r.ReceivedBytes > UdpCodec.MaxPayload) continue;

            var visitor = (IPEndPoint)r.RemoteEndPoint;
            if (!port.ByEndpoint.TryGetValue(visitor, out var flow))
            {
                flow = new Flow(Interlocked.Increment(ref _nextFlow), visitor);
                port.ByEndpoint[visitor] = flow;
                port.ById[flow.Id] = flow;
                Log.Debug($"[{_tag}] udp :{port.Port} new flow {flow.Id} from {visitor}");
            }
            flow.Touch();

            int n = _codec.Encode(UdpCodec.Data, port.Id, flow.Id, rx.AsSpan(0, r.ReceivedBytes), tx);
            _relay.Send(tx.AsSpan(0, n), client);
        }
    }

    /// <summary>Client -> visitor (or keepalive). Runs on the relay receive loop.</summary>
    public void OnRelayPacket(Span<byte> packet, IPEndPoint from)
    {
        if (!_codec.TryDecode(packet, out var d)) return;
        switch (d.Type)
        {
            case UdpCodec.Keepalive:
                lock (_gate)
                {
                    // Only a newer keepalive may move the endpoint, so a replayed old packet can't hijack it.
                    if (d.Counter > _highestKeepalive)
                    {
                        _highestKeepalive = d.Counter;
                        if (!from.Equals(_client))
                        {
                            Log.Info($"[{_tag}] udp relay connected from {from}");
                            Volatile.Write(ref _client, from);
                        }
                    }
                }
                Span<byte> ack = stackalloc byte[UdpCodec.Overhead];
                int n = _codec.Encode(UdpCodec.KeepaliveAck, 0, 0, [], ack);
                _relay.Send(ack[..n], from);
                break;

            case UdpCodec.Data:
                if (d.Tunnel >= _ports.Count) return;
                var port = _ports[d.Tunnel];
                if (!port.ById.TryGetValue(d.Flow, out var flow)) return;
                flow.Touch();
                try { port.Socket.SendTo(packet.Slice(d.PayloadOffset, d.PayloadLength), SocketFlags.None, flow.Endpoint); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                break;
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var port in _ports)
                foreach (var flow in port.ById.Values)
                {
                    if (flow.IdleFor < FlowIdle) continue;
                    port.ById.TryRemove(flow.Id, out _);
                    port.ByEndpoint.TryRemove(KeyValuePair.Create(flow.Endpoint, flow));
                    Log.Debug($"[{_tag}] udp :{port.Port} flow {flow.Id} expired");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _relay.Unregister(Id, this);
        foreach (var p in _ports)
        {
            try { p.Socket.Dispose(); } catch { }
        }
        _codec.Dispose();
    }

    private sealed class PublicPort(ushort id, Socket socket, int port, string tunnel)
    {
        public ushort Id { get; } = id;
        public Socket Socket { get; } = socket;
        public int Port { get; } = port;
        public string Tunnel { get; } = tunnel;
        public ConcurrentDictionary<uint, Flow> ById { get; } = new();
        public ConcurrentDictionary<IPEndPoint, Flow> ByEndpoint { get; } = new();
    }

    private sealed class Flow(uint id, IPEndPoint endpoint)
    {
        private long _lastSeen = Environment.TickCount64;
        public uint Id { get; } = id;
        public IPEndPoint Endpoint { get; } = endpoint;
        public void Touch() => Volatile.Write(ref _lastSeen, Environment.TickCount64);
        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastSeen));
    }
}
