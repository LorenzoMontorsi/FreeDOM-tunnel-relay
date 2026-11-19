using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace FreeDom.Core;

/// <summary>A socket plus the stream used to talk over it (NetworkStream or SslStream).</summary>
public sealed class Conn(Socket socket, Stream stream) : IDisposable
{
    private int _disposed;

    public Socket Socket { get; } = socket;
    public Stream Stream { get; } = stream;

    public static Conn FromSocket(Socket socket) => new(socket, new NetworkStream(socket, ownsSocket: true));

    /// <summary>Half-close: signal EOF to the peer while still being able to read.</summary>
    public async Task ShutdownWriteAsync()
    {
        try
        {
            if (Stream is SslStream ssl) await ssl.ShutdownAsync().ConfigureAwait(false);
            Socket.Shutdown(SocketShutdown.Send);
        }
        catch { /* peer already gone */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Stream.Dispose(); } catch { }
        try { Socket.Dispose(); } catch { }
    }
}

public static class Net
{
    public static void Tune(Socket socket)
    {
        try { socket.NoDelay = true; } catch { }
        try
        {
            // Detect dead peers on long-lived idle connections without imposing any idle limit.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch { }
    }

    public static async Task<Socket> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            Tune(socket);
            return socket;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"timed out connecting to {host}:{port}");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static TcpListener Listen(string bind, int port)
    {
        var ip = IPAddress.Parse(bind);
        var listener = new TcpListener(ip, port);
        if (ip.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start(1024);
        return listener;
    }

    /// <summary>
    /// Parses "3000", "host:3000", "[::1]:3000", "http://host:3000/path".
    /// A bare number means a port on <paramref name="defaultHost"/>.
    /// </summary>
    public static (string Host, int Port) ParseHostPort(string value, string defaultHost, int? defaultPort)
    {
        string v = value.Trim();
        int scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) v = v[(scheme + 3)..];
        int slash = v.IndexOf('/');
        if (slash >= 0) v = v[..slash];
        if (v.Length == 0) throw new FormatException($"invalid address '{value}'");

        string host;
        string? portText;
        if (v.All(char.IsAsciiDigit))
        {
            host = defaultHost;
            portText = v;
        }
        else if (v.StartsWith('['))
        {
            int end = v.IndexOf(']');
            if (end < 0) throw new FormatException($"invalid address '{value}'");
            host = v[1..end];
            portText = end + 1 < v.Length && v[end + 1] == ':' ? v[(end + 2)..] : null;
        }
        else if (v.Count(c => c == ':') == 1)
        {
            int colon = v.IndexOf(':');
            host = v[..colon];
            portText = v[(colon + 1)..];
        }
        else
        {
            host = v; // hostname or bare IPv6 literal without port
            portText = null;
        }

        int port;
        if (portText is null)
            port = defaultPort ?? throw new FormatException($"missing port in '{value}'");
        else if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            throw new FormatException($"invalid port in '{value}'");

        if (host.Length == 0) host = defaultHost;
        return (host, port);
    }

    public static string Describe(Exception ex) => ex switch
    {
        EndOfStreamException => "connection closed by peer",
        SocketException se => $"{se.SocketErrorCode}: {se.Message}",
        _ => ex.Message,
    };
}

/// <summary>Bidirectional byte pump with half-close support. No limits, no idle timeout.</summary>
public static class Bridge
{
    private const int BufferSize = 64 * 1024;

    public static async Task RunAsync(Conn a, Conn b)
    {
        try
        {
            await Task.WhenAll(PumpAsync(a, b), PumpAsync(b, a)).ConfigureAwait(false);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private static async Task PumpAsync(Conn from, Conn to)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int n = await from.Stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0) break;
                await to.Stream.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            }
            await to.Stream.FlushAsync().ConfigureAwait(false);
            await to.ShutdownWriteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Reset/abort on either side: tear down both so the opposite pump unblocks.
            from.Dispose();
            to.Dispose();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public static class HttpText
{
    /// <summary>Writes a small plain-text HTTP response, then closes the connection gracefully.</summary>
    public static async Task RespondAsync(Conn conn, int code, string reason, string body)
    {
        try
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body + "\n");
            string head =
                $"HTTP/1.1 {code} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "Server: FreeDOM\r\n\r\n";
            await conn.Stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await conn.Stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            await conn.Stream.FlushAsync().ConfigureAwait(false);
            await conn.ShutdownWriteAsync().ConfigureAwait(false);

            // Drain briefly so closing with unread data doesn't RST away our response.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] scratch = new byte[4096];
            while (await conn.Stream.ReadAsync(scratch, cts.Token).ConfigureAwait(false) > 0) { }
        }
        catch { }
        finally
        {
            conn.Dispose();
        }
    }
}
