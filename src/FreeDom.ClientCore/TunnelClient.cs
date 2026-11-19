using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using FreeDom.Core;

namespace FreeDom.ClientCore;

public class FatalException(string message) : Exception(message);

/// <summary>The server certificate was rejected; <see cref="Fingerprint"/> is what the server presented.</summary>
public sealed class CertificateRejectedException(string message, string? fingerprint, bool mismatch) : FatalException(message)
{
    public string? Fingerprint { get; } = fingerprint;
    /// <summary>True when a fingerprint was configured but the server presented a different one.</summary>
    public bool Mismatch { get; } = mismatch;
}

public sealed class ClientSettings
{
    public required string ServerHost { get; init; }
    public required int ServerPort { get; init; }
    public string? Token { get; init; }
    public bool Tls { get; init; } = true;
    public bool Insecure { get; init; }
    public string? Fingerprint { get; init; }
    public required List<ResolvedTunnel> Tunnels { get; init; }
}

public enum ClientState { Connecting, Online, Reconnecting, Stopped, Failed }

/// <summary>A tunnel as confirmed by the server.</summary>
public sealed record ActiveTunnel(string Name, string Type, string PublicUrl, string Local);

public sealed class TunnelClient(ClientSettings settings)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ResolvedTunnel> _byName =
        settings.Tunnels.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    private readonly string _clientId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private readonly string? _fingerprint = settings.Fingerprint?.Replace(":", "").Replace(" ", "").ToUpperInvariant();
    private bool _established;
    private int _activeConnections;

    /// <summary>Raised on state changes, with an optional human-readable detail (e.g. the error).</summary>
    public event Action<ClientState, string?>? StateChanged;

    /// <summary>Raised when the server confirms the tunnels (after every (re)connect).</summary>
    public event Action<IReadOnlyList<ActiveTunnel>>? TunnelsOnline;

    /// <summary>Raised when the number of proxied connections changes.</summary>
    public event Action<int>? ConnectionsChanged;

    /// <summary>Raised before a Failed state when the server certificate could not be trusted.</summary>
    public event Action<CertificateRejectedException>? CertificateRejected;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        StateChanged?.Invoke(ClientState.Connecting, null);
        while (!ct.IsCancellationRequested)
        {
            _established = false;
            string? reason = null;
            try
            {
                await RunSessionAsync(ct);
            }
            catch (FatalException ex)
            {
                Log.Error(ex.Message);
                if (ex is CertificateRejectedException { Fingerprint: not null } cre) CertificateRejected?.Invoke(cre);
                StateChanged?.Invoke(ClientState.Failed, ex.Message);
                return 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = Net.Describe(ex);
                Log.Warn($"{(_established ? "Disconnected" : "Connection failed")}: {reason}");
            }

            if (_established) delay = TimeSpan.FromSeconds(1);
            Log.Info($"Reconnecting in {delay.TotalSeconds:0}s...");
            StateChanged?.Invoke(ClientState.Reconnecting, reason);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
            StateChanged?.Invoke(ClientState.Connecting, null);
        }
        StateChanged?.Invoke(ClientState.Stopped, null);
        return 0;
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var conn = await ConnectServerAsync(ct);
        var channel = new ControlChannel(conn.Stream);

        await channel.SendAsync(new Message
        {
            Type = MsgType.Hello,
            Version = Proto.Version,
            Token = settings.Token,
            ClientId = _clientId,
            Tunnels = settings.Tunnels.Select(t => t.Request).ToList(),
        }, ct);

        Message response;
        using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hs.CancelAfter(Proto.HandshakeTimeout);
            try
            {
                response = await channel.ReceiveAsync(hs.Token);
            }
            catch (EndOfStreamException)
            {
                throw new IOException("server closed the connection during the handshake (TLS on/off mismatch?)");
            }
        }

        if (response.Type == MsgType.Error)
        {
            string text = $"server refused the tunnels: {response.Error}";
            if (response.Fatal) throw new FatalException(text);
            throw new IOException(text);
        }
        if (response.Type != MsgType.HelloOk) throw new IOException($"unexpected server response '{response.Type}'");

        _established = true;
        var active = (response.Assigned ?? [])
            .Select(a => new ActiveTunnel(a.Name, a.Type, a.PublicUrl,
                _byName.TryGetValue(a.Name, out var t) ? t.LocalDisplay : "?"))
            .ToList();
        Log.Info($"Connected to {settings.ServerHost}:{settings.ServerPort} - tunnels online:");
        foreach (var a in active) Log.Info($"    {a.Name,-10} {a.PublicUrl}  ->  {a.Local}");
        StateChanged?.Invoke(ClientState.Online, null);
        TunnelsOnline?.Invoke(active);

        // UDP tunnels ride a separate encrypted UDP channel, alive as long as this control session.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        UdpClientRelay? udp = null;
        Task? udpTask = null;
        var udpTunnels = (response.Assigned ?? [])
            .Where(a => a.Type == "udp" && _byName.ContainsKey(a.Name))
            .Select(a => ((ushort)a.Id, _byName[a.Name]))
            .ToList();
        if (udpTunnels.Count > 0)
        {
            if (response.UdpKey is null || response.UdpPort == 0)
                throw new FatalException("the server did not provide a UDP relay (server too old or started with --no-udp)");
            udp = await UdpClientRelay.CreateAsync(settings.ServerHost, response.UdpPort, response.UdpSession,
                Convert.FromBase64String(response.UdpKey), udpTunnels, ct);
            udpTask = udp.RunAsync(sessionCts.Token);
        }

        try
        {
            await ControlLoopAsync(channel, ct);
        }
        finally
        {
            sessionCts.Cancel();
            udp?.Dispose();
            if (udpTask is not null)
            {
                try { await udpTask; } catch { }
            }
        }
    }

    private async Task ControlLoopAsync(ControlChannel channel, CancellationToken ct)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(Proto.IdleTimeout);
            Message msg;
            try
            {
                msg = await channel.ReceiveAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("server heartbeat timed out");
            }

            switch (msg.Type)
            {
                case MsgType.Ping:
                    await channel.SendAsync(new Message { Type = MsgType.Pong }, ct);
                    break;
                case MsgType.NewConn when msg.ConnId is not null && msg.Tunnel is not null:
                    _ = HandleNewConnAsync(msg.ConnId, msg.Tunnel, ct);
                    break;
            }
        }
    }

    private async Task HandleNewConnAsync(string connId, string tunnelName, CancellationToken ct)
    {
        if (!_byName.TryGetValue(tunnelName, out var tunnel)) return;
        ConnectionsChanged?.Invoke(Interlocked.Increment(ref _activeConnections));
        Conn? data = null;
        try
        {
            data = await ConnectServerAsync(ct);
            await Frames.WriteAsync(data.Stream, new Message { Type = MsgType.Data, ConnId = connId }, ct);

            Conn local;
            try
            {
                local = Conn.FromSocket(await Net.ConnectAsync(tunnel.LocalHost, tunnel.LocalPort, TimeSpan.FromSeconds(10), ct));
            }
            catch (Exception ex)
            {
                Log.Warn($"[{tunnel.Name}] cannot reach {tunnel.LocalDisplay}: {Net.Describe(ex)}");
                if (tunnel.Type == "http")
                    await HttpText.RespondAsync(data, 502, "Bad Gateway",
                        $"FreeDOM: the tunnel is online but nothing answers at {tunnel.LocalDisplay} on the client machine.");
                data.Dispose();
                return;
            }

            Log.Debug($"[{tunnel.Name}] new connection -> {tunnel.LocalDisplay}");
            await Bridge.RunAsync(data, local);
        }
        catch (Exception ex)
        {
            Log.Debug($"[{tunnel.Name}] data connection failed: {Net.Describe(ex)}");
            data?.Dispose();
        }
        finally
        {
            ConnectionsChanged?.Invoke(Interlocked.Decrement(ref _activeConnections));
        }
    }

    private async Task<Conn> ConnectServerAsync(CancellationToken ct)
    {
        var socket = await Net.ConnectAsync(settings.ServerHost, settings.ServerPort, TimeSpan.FromSeconds(15), ct);
        var conn = Conn.FromSocket(socket);
        if (!settings.Tls) return conn;

        bool rejected = false;
        string? seen = null;
        var ssl = new SslStream(conn.Stream, leaveInnerStreamOpen: false);
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(Proto.HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = settings.ServerHost,
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    seen = cert?.GetCertHashString(HashAlgorithmName.SHA256);
                    bool ok = settings.Insecure
                              || (_fingerprint is not null ? string.Equals(seen, _fingerprint, StringComparison.OrdinalIgnoreCase)
                                                           : errors == SslPolicyErrors.None);
                    rejected = !ok;
                    return ok;
                },
            }, hs.Token);
            return new Conn(socket, ssl);
        }
        catch (AuthenticationException) when (rejected)
        {
            conn.Dispose();
            throw new CertificateRejectedException(_fingerprint is not null
                ? $"server certificate fingerprint {seen} does not match the expected {_fingerprint}"
                : $"server certificate is not trusted (self-signed?). Use fingerprint {seen} " +
                  "after checking it matches the one printed in the server log.", seen, _fingerprint is not null);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }
}
