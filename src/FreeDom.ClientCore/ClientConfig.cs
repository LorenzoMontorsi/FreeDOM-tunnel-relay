using System.Text.Json;
using System.Text.Json.Serialization;
using FreeDom.Core;

namespace FreeDom.ClientCore;

public sealed class ClientConfig
{
    public string? Server { get; set; }
    public string? Token { get; set; }
    public bool? Tls { get; set; }
    public bool Insecure { get; set; }
    public string? Fingerprint { get; set; }
    public List<ClientTunnel> Tunnels { get; set; } = [];

    // GUI preferences (ignored by the CLI).
    public bool AutoConnect { get; set; }
    public bool? MinimizeToTray { get; set; }

    public static ClientConfig Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), ClientJson.Default.ClientConfig)
        ?? throw new InvalidDataException($"{path} is empty");

    public void Save(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ClientJson.Default.ClientConfig));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // contains the token
    }

    /// <summary>Per-user config location (e.g. %APPDATA%\FreeDOM, ~/.config/FreeDOM).</summary>
    public static string DefaultUserPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDOM", "freedom.json");

    /// <summary>Builds runtime settings, validating everything. Throws ArgumentException/FormatException.</summary>
    public ClientSettings ToSettings()
    {
        if (string.IsNullOrWhiteSpace(Server)) throw new ArgumentException("no server configured");
        var (host, port) = Net.ParseHostPort(Server, "", Proto.DefaultControlPort);
        if (host.Length == 0) throw new ArgumentException($"invalid server address '{Server}'");
        var active = Tunnels.Where(t => !t.Disabled).ToList();
        if (active.Count == 0) throw new ArgumentException("no enabled tunnels defined");

        var tunnels = active.Select(ResolvedTunnel.From).ToList();
        var dupe = tunnels.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null) throw new ArgumentException($"duplicate tunnel name '{dupe.Key}'");

        return new ClientSettings
        {
            ServerHost = host,
            ServerPort = port,
            Token = string.IsNullOrEmpty(Token) ? null : Token,
            Tls = Tls ?? true,
            Insecure = Insecure,
            Fingerprint = string.IsNullOrWhiteSpace(Fingerprint) ? null : Fingerprint.Trim(),
            Tunnels = tunnels,
        };
    }
}

public sealed class ClientTunnel
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "http";
    /// <summary>Local target: "3000", "localhost:3000", "192.168.1.10:80".</summary>
    public string Local { get; set; } = "";
    public string? Subdomain { get; set; }
    public string? Hostname { get; set; }
    public int RemotePort { get; set; }
    /// <summary>Lets the GUI keep a tunnel in the config without starting it.</summary>
    public bool Disabled { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(ClientConfig))]
internal partial class ClientJson : JsonSerializerContext;

/// <summary>A tunnel with its local target resolved.</summary>
public sealed class ResolvedTunnel
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string LocalHost { get; init; }
    public required int LocalPort { get; init; }
    public required TunnelRequest Request { get; init; }
    public string LocalDisplay => LocalHost.Contains(':') ? $"[{LocalHost}]:{LocalPort}" : $"{LocalHost}:{LocalPort}";

    public static ResolvedTunnel From(ClientTunnel t, int index)
    {
        string type = t.Type.Trim().ToLowerInvariant();
        string label = string.IsNullOrWhiteSpace(t.Name) ? $"#{index + 1}" : t.Name;
        if (type is not ("http" or "tcp" or "udp")) throw new ArgumentException($"tunnel '{label}': type must be http, tcp or udp");
        if (string.IsNullOrWhiteSpace(t.Local)) throw new ArgumentException($"tunnel '{label}': missing local address");
        var (host, port) = Net.ParseHostPort(t.Local, "localhost", type == "http" ? 80 : null);
        string name = string.IsNullOrWhiteSpace(t.Name) ? (index == 0 ? type : $"{type}{index + 1}") : t.Name.Trim();
        return new ResolvedTunnel
        {
            Name = name,
            Type = type,
            LocalHost = host,
            LocalPort = port,
            Request = new TunnelRequest
            {
                Name = name,
                Type = type,
                Subdomain = string.IsNullOrWhiteSpace(t.Subdomain) ? null : t.Subdomain.Trim(),
                Hostname = string.IsNullOrWhiteSpace(t.Hostname) ? null : t.Hostname.Trim(),
                RemotePort = t.RemotePort,
            },
        };
    }
}
