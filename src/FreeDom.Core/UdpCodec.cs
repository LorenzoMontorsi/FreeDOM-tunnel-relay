using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FreeDom.Core;

/// <summary>
/// Encrypted datagram format for the UDP relay between client and server.
/// <code>
/// [session u32][counter u64] | AES-GCM( [type u8][tunnel u16][flow u32][payload...] ) | [tag 16]
/// </code>
/// The 12 plaintext header bytes are authenticated as associated data. The nonce is
/// [direction u8][0 0 0][counter u64], so both sides can share one key safely.
/// </summary>
public sealed class UdpCodec : IDisposable
{
    public const int HeaderSize = 12;
    public const int InnerHeaderSize = 7;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + InnerHeaderSize + TagSize;
    public const int MaxDatagram = 65507;
    public const int MaxPayload = MaxDatagram - Overhead;

    public const byte Keepalive = 1;
    public const byte KeepaliveAck = 2;
    public const byte Data = 3;

    private readonly AesGcm _aes;
    private readonly uint _session;
    private readonly byte _sendDirection;
    private readonly byte _receiveDirection;
    private long _counter;

    public UdpCodec(byte[] key, uint session, bool isServer)
    {
        _aes = new AesGcm(key, TagSize);
        _session = session;
        _sendDirection = isServer ? (byte)1 : (byte)0;
        _receiveDirection = isServer ? (byte)0 : (byte)1;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static bool TryPeekSession(ReadOnlySpan<byte> packet, out uint session)
    {
        session = 0;
        if (packet.Length < Overhead) return false;
        session = BinaryPrimitives.ReadUInt32BigEndian(packet);
        return true;
    }

    /// <summary>Builds an encrypted datagram into <paramref name="destination"/> and returns its length.</summary>
    public int Encode(byte type, ushort tunnel, uint flow, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        ulong counter = (ulong)Interlocked.Increment(ref _counter);
        BinaryPrimitives.WriteUInt32BigEndian(destination, _session);
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], counter);

        int plainLength = InnerHeaderSize + payload.Length;
        var body = destination.Slice(HeaderSize, plainLength);
        body[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(body[1..], tunnel);
        BinaryPrimitives.WriteUInt32BigEndian(body[3..], flow);
        payload.CopyTo(body[InnerHeaderSize..]);

        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        nonce[0] = _sendDirection;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        var tag = destination.Slice(HeaderSize + plainLength, TagSize);
        lock (_aes) _aes.Encrypt(nonce, body, body, tag, destination[..HeaderSize]);
        return HeaderSize + plainLength + TagSize;
    }

    /// <summary>Decrypts in place. On success the payload is packet[PayloadOffset..PayloadOffset+PayloadLength].</summary>
    public bool TryDecode(Span<byte> packet, out Decoded result)
    {
        result = default;
        if (packet.Length < Overhead) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet) != _session) return false;
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(packet[4..]);

        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        nonce[0] = _receiveDirection;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        int bodyLength = packet.Length - HeaderSize - TagSize;
        var body = packet.Slice(HeaderSize, bodyLength);
        var tag = packet.Slice(HeaderSize + bodyLength, TagSize);
        try
        {
            lock (_aes) _aes.Decrypt(nonce, body, tag, body, packet[..HeaderSize]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        result = new Decoded(
            body[0],
            BinaryPrimitives.ReadUInt16BigEndian(body[1..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[3..]),
            counter,
            HeaderSize + InnerHeaderSize,
            bodyLength - InnerHeaderSize);
        return true;
    }

    public void Dispose() => _aes.Dispose();

    public readonly record struct Decoded(byte Type, ushort Tunnel, uint Flow, ulong Counter, int PayloadOffset, int PayloadLength);
}

public static class UdpSockets
{
    /// <summary>
    /// On Windows an ICMP "port unreachable" (e.g. the game server restarting) makes the next
    /// receive on the UDP socket throw ConnectionReset forever unless this is turned off.
    /// </summary>
    public static void DisableConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch { }
    }

    public static void Tune(Socket socket)
    {
        DisableConnReset(socket);
        try { socket.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        try { socket.SendBufferSize = 4 * 1024 * 1024; } catch { }
    }

    public static Socket Bind(string bind, int port)
    {
        var ip = System.Net.IPAddress.Parse(bind);
        var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (ip.Equals(System.Net.IPAddress.IPv6Any)) socket.DualMode = true;
            Tune(socket);
            socket.Bind(new System.Net.IPEndPoint(ip, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static System.Net.EndPoint AnyEndPoint(Socket socket) =>
        new System.Net.IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6
            ? System.Net.IPAddress.IPv6Any : System.Net.IPAddress.Any, 0);

    /// <summary>
    /// Send on a connected UDP socket. A pending ICMP error from an earlier datagram (e.g. the
    /// game server was down for a moment) is reported on the next send, so retry once.
    /// </summary>
    public static void Send(Socket socket, ReadOnlySpan<byte> datagram)
    {
        try
        {
            socket.Send(datagram);
        }
        catch (SocketException ex) when (IsTransient(ex))
        {
            try { socket.Send(datagram); }
            catch (SocketException) { }
        }
    }

    /// <summary>Errors caused by ICMP feedback that must not stop a UDP receive loop.</summary>
    public static bool IsTransient(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.MessageSize;
}
