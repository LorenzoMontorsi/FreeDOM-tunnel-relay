using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeDom.Core;

/// <summary>Protocol constants shared by client and server.</summary>
public static class Proto
{
    public const int Version = 1;
    public const int DefaultControlPort = 7835;
    public const int MaxFrame = 1 << 20;

    /// <summary>How often the server pings the client on the control channel.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>A control channel with no traffic for this long is considered dead.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Max time to complete the TLS + hello handshake.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Max time the client gets to open a data connection after a new_conn request.</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
}

public static class MsgType
{
    public const string Hello = "hello";        // client -> server, first frame of a control connection
    public const string HelloOk = "hello_ok";   // server -> client
    public const string Error = "error";        // server -> client
    public const string NewConn = "new_conn";   // server -> client: please open a data connection
    public const string Data = "data";          // client -> server, first frame of a data connection
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public sealed class TunnelRequest
{
    public string Name { get; set; } = "";
    /// <summary>"http" or "tcp".</summary>
    public string Type { get; set; } = "http";
    public string? Subdomain { get; set; }
    public string? Hostname { get; set; }
    public int RemotePort { get; set; }
}

public sealed class TunnelInfo
{
    /// <summary>For UDP tunnels: the id used in relay packets.</summary>
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string PublicUrl { get; set; } = "";
}

/// <summary>Single envelope type for every control message; unused fields are omitted on the wire.</summary>
public sealed class Message
{
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public string? Token { get; set; }
    public string? ClientId { get; set; }
    public List<TunnelRequest>? Tunnels { get; set; }
    public List<TunnelInfo>? Assigned { get; set; }
    public string? ConnId { get; set; }
    public string? Tunnel { get; set; }
    public string? Error { get; set; }
    /// <summary>On errors: true when retrying cannot help (bad token, invalid request).</summary>
    public bool Fatal { get; set; }

    // UDP relay parameters, sent in hello_ok when the client asked for UDP tunnels.
    public int UdpPort { get; set; }
    public uint UdpSession { get; set; }
    /// <summary>Base64 AES-256-GCM key for the UDP relay of this session.</summary>
    public string? UdpKey { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(Message))]
internal partial class ProtocolJson : JsonSerializerContext;

/// <summary>Length-prefixed (4 byte big-endian) JSON frames.</summary>
public static class Frames
{
    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.Message);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads exactly one frame and nothing more, so the stream can be used raw afterwards.</summary>
    public static async Task<Message> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > Proto.MaxFrame)
            throw new InvalidDataException($"invalid frame length {length}");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, ProtocolJson.Default.Message)
               ?? throw new InvalidDataException("empty message");
    }
}

/// <summary>Control connection wrapper: serializes concurrent writers.</summary>
public sealed class ControlChannel(Stream stream)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SendAsync(Message message, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await Frames.WriteAsync(stream, message, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public Task<Message> ReceiveAsync(CancellationToken ct) => Frames.ReadAsync(stream, ct);
}
