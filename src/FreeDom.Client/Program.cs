using System.Runtime.InteropServices;
using FreeDom.ClientCore;
using FreeDom.Core;

const string Help = """
    FreeDOM client - expose a local server through your own FreeDOM server

    Usage:
      freedom-cli http <local> [--subdomain <name> | --hostname <host>]   expose a local HTTP server
      freedom-cli tcp  <local> [--remote-port <n>]                          expose any TCP service
      freedom-cli udp  <local> [--remote-port <n>]                          expose a UDP service (game servers)
      freedom-cli start [--config freedom.json]                             start every tunnel in the config file
      freedom-cli init  [--config freedom.json]                             write an example config file

      <local> is a port (3000) or host:port (192.168.1.10:80).

    Connection options (override the config file; env FREEDOM_SERVER / FREEDOM_TOKEN also work):
      --server <host[:port]>   FreeDOM server address (default port 7835)
      --token <secret>         Server token
      --fingerprint <sha256>   Trust the server's self-signed certificate with this fingerprint
      --insecure               Skip certificate validation entirely (testing only)
      --no-tls                 Plain TCP control channel (server must run with --tls off)
      --name <name>            Tunnel name (http/tcp/udp commands)
      --config <path>          Config file (default ./freedom.json when present)
      --verbose                Log every connection
    """;

CliArgs a;
try
{
    a = CliArgs.Parse(args,
        ["server", "token", "fingerprint", "config", "subdomain", "hostname", "remote-port", "name"],
        ["insecure", "no-tls", "verbose"]);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}\n\nRun 'freedom-cli --help' for usage.");
    return 2;
}

string command = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "";
if (a.Has("help") || command is "" or "help")
{
    Console.WriteLine(Help);
    return 0;
}

Log.Verbose = a.Has("verbose");
string configPath = a.Get("config") ?? "freedom.json";

if (command == "init")
{
    if (File.Exists(configPath))
    {
        Console.Error.WriteLine($"error: {configPath} already exists");
        return 1;
    }
    new ClientConfig
    {
        Server = "your-vps.example.com:7835",
        Token = "change-me",
        Fingerprint = "PASTE-SERVER-FINGERPRINT-HERE",
        Tunnels =
        [
            new ClientTunnel { Name = "web", Type = "http", Local = "3000", Subdomain = "myapp" },
            new ClientTunnel { Name = "ssh", Type = "tcp", Local = "22", RemotePort = 2222 },
        ],
    }.Save(configPath);
    Console.WriteLine($"Wrote {Path.GetFullPath(configPath)}");
    return 0;
}

ClientSettings settings;
try
{
    var config = File.Exists(configPath) ? ClientConfig.Load(configPath)
        : a.Get("config") is not null ? throw new ArgumentException($"config file {configPath} not found")
        : new ClientConfig();

    var tunnels = new List<ClientTunnel>();
    switch (command)
    {
        case "http" or "tcp" or "udp":
            if (a.Positional.Count != 2) throw new ArgumentException($"usage: freedom-cli {command} <local>");
            tunnels.Add(new ClientTunnel
            {
                Name = a.Get("name") ?? command,
                Type = command,
                Local = a.Positional[1],
                Subdomain = a.Get("subdomain"),
                Hostname = a.Get("hostname"),
                RemotePort = a.GetInt("remote-port") ?? 0,
            });
            break;
        case "start":
            tunnels.AddRange(config.Tunnels.Where(t => !t.Disabled));
            if (tunnels.Count == 0) throw new ArgumentException($"no enabled tunnels defined in {configPath}");
            break;
        default:
            throw new ArgumentException($"unknown command '{command}'");
    }

    string server = a.Get("server") ?? config.Server ?? Environment.GetEnvironmentVariable("FREEDOM_SERVER")
        ?? throw new ArgumentException("no server configured: pass --server <host[:port]>");
    var (host, port) = Net.ParseHostPort(server, "", Proto.DefaultControlPort);
    if (host.Length == 0) throw new ArgumentException($"invalid server address '{server}'");

    settings = new ClientSettings
    {
        ServerHost = host,
        ServerPort = port,
        Token = a.Get("token") ?? config.Token ?? Environment.GetEnvironmentVariable("FREEDOM_TOKEN"),
        Tls = !a.Has("no-tls") && (config.Tls ?? true),
        Insecure = a.Has("insecure") || config.Insecure,
        Fingerprint = a.Get("fingerprint") ?? config.Fingerprint,
        Tunnels = tunnels.Select(ResolvedTunnel.From).ToList(),
    };

    var dupes = settings.Tunnels.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (dupes is not null) throw new ArgumentException($"duplicate tunnel name '{dupes.Key}'");
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

if (!settings.Tls) Log.Warn("TLS disabled: token and traffic travel unencrypted to the server.");
else if (settings.Insecure) Log.Warn("Certificate validation disabled (--insecure).");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

return await new TunnelClient(settings).RunAsync(cts.Token);
