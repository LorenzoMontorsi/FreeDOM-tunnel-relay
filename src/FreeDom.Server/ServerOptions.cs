using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FreeDom.Core;

namespace FreeDom.Server;

sealed class ServerOptions
{
    public string? Domain { get; private set; }
    public string? PublicHost { get; private set; }
    public string PublicScheme { get; private set; } = "https";
    public int? PublicPort { get; private set; }

    public string? Token { get; private set; }
    public bool NoAuth { get; private set; }

    public string ControlBind { get; private set; } = "0.0.0.0";
    public int ControlPort { get; private set; } = Proto.DefaultControlPort;
    public string HttpBind { get; private set; } = "0.0.0.0";
    public int HttpPort { get; private set; } = 8080;
    public int AskPort { get; private set; }

    public string TcpBind { get; private set; } = "0.0.0.0";
    public int TcpMin { get; private set; } = 1024;
    public int TcpMax { get; private set; } = 65535;

    public string Tls { get; private set; } = "auto";
    public string? TlsCert { get; private set; }
    public string? TlsKey { get; private set; }
    public string TlsFile { get; private set; } = "freedom-server.pfx";

    public bool Verbose { get; private set; }

    public const string Help = """
        FreeDOM server - self-hosted tunnel server

        Usage: freedom-server --domain tunnel.example.com --token SECRET [options]

          --domain <name>          Base domain for HTTP tunnels (subdomains become <sub>.<domain>)  [env FREEDOM_DOMAIN]
          --token <secret>         Shared secret clients must present                                [env FREEDOM_TOKEN]
          --no-auth                Accept any client (not recommended on a public server)

          --control-port <n>       Port clients connect to                         (default 7835)
          --control-bind <ip>      Bind address for the control port               (default 0.0.0.0)
          --http-port <n>          Port of the HTTP front (0 = disabled)           (default 8080)
          --http-bind <ip>         Bind address for the HTTP front; use 127.0.0.1 behind Caddy/nginx
          --ask-port <n>           Caddy on-demand TLS "ask" endpoint on 127.0.0.1 (default off)

          --tcp-ports <min-max>    Port range allowed for TCP tunnels              (default 1024-65535)
          --tcp-bind <ip>          Bind address for TCP tunnels                    (default 0.0.0.0)

          --tls <auto|off>         Control channel TLS. auto = self-signed cert stored in --tls-file (default auto)
          --tls-file <path>        Where the auto-generated certificate lives      (default freedom-server.pfx)
          --tls-cert <pem>         Use this certificate instead (with --tls-key)
          --tls-key <pem>

          --public-scheme <s>      Scheme shown in tunnel URLs                     (default https)
          --public-port <n>        Port shown in tunnel URLs (omit for 80/443)
          --public-host <name>     Host shown for TCP tunnel URLs                  (default --domain)
          --verbose                Log every connection
        """;

    public static ServerOptions? Parse(string[] args)
    {
        var a = CliArgs.Parse(args,
            ["domain", "public-host", "public-scheme", "public-port", "token", "control-bind", "control-port",
             "http-bind", "http-port", "ask-port", "tcp-bind", "tcp-ports", "tls", "tls-cert", "tls-key", "tls-file"],
            ["no-auth", "verbose"]);

        if (a.Has("help")) { Console.WriteLine(Help); return null; }
        if (a.Positional.Count > 0) throw new ArgumentException($"unexpected argument '{a.Positional[0]}'");

        var o = new ServerOptions
        {
            Domain = Normalize(a.Get("domain") ?? Environment.GetEnvironmentVariable("FREEDOM_DOMAIN")),
            Token = a.Get("token") ?? Environment.GetEnvironmentVariable("FREEDOM_TOKEN"),
            NoAuth = a.Has("no-auth"),
            PublicScheme = a.Get("public-scheme") ?? "https",
            PublicPort = a.GetInt("public-port"),
            ControlBind = a.Get("control-bind") ?? "0.0.0.0",
            ControlPort = a.GetInt("control-port") ?? Proto.DefaultControlPort,
            HttpBind = a.Get("http-bind") ?? "0.0.0.0",
            HttpPort = a.GetInt("http-port") ?? 8080,
            AskPort = a.GetInt("ask-port") ?? 0,
            TcpBind = a.Get("tcp-bind") ?? "0.0.0.0",
            Tls = (a.Get("tls") ?? "auto").ToLowerInvariant(),
            TlsCert = a.Get("tls-cert"),
            TlsKey = a.Get("tls-key"),
            TlsFile = a.Get("tls-file") ?? "freedom-server.pfx",
            Verbose = a.Has("verbose"),
        };
        o.PublicHost = Normalize(a.Get("public-host")) ?? o.Domain;

        if (a.Get("tcp-ports") is { } range)
        {
            var parts = range.Split('-', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int min) || !int.TryParse(parts[1], out int max)
                || min < 1 || max > 65535 || min > max)
                throw new ArgumentException($"invalid --tcp-ports '{range}', expected e.g. 20000-30000");
            o.TcpMin = min;
            o.TcpMax = max;
        }

        if (string.IsNullOrEmpty(o.Token) && !o.NoAuth)
            throw new ArgumentException("no token configured: pass --token <secret> (or FREEDOM_TOKEN), or --no-auth");
        if (o.Tls is not ("auto" or "off"))
            throw new ArgumentException("--tls must be 'auto' or 'off'");
        if ((o.TlsCert is null) != (o.TlsKey is null))
            throw new ArgumentException("--tls-cert and --tls-key must be used together");
        return o;
    }

    public static string? Normalize(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : host.Trim().TrimEnd('.').ToLowerInvariant();

    public bool CheckToken(string? presented)
    {
        if (NoAuth) return true;
        if (presented is null || Token is null) return false;
        // Hash both so the comparison is constant-time regardless of length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Token)));
    }

    public X509Certificate2? LoadCertificate()
    {
        if (TlsCert is not null)
        {
            using var pem = X509Certificate2.CreateFromPemFile(TlsCert, TlsKey);
            // Re-import so the private key is usable by SslStream on every platform.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        if (Tls == "off") return null;

        if (File.Exists(TlsFile))
            return X509CertificateLoader.LoadPkcs12FromFile(TlsFile, null);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=FreeDOM tunnel server", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Domain ?? "freedom.local");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        byte[] pfx = cert.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(TlsFile, pfx);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TlsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Log.Info($"Generated self-signed control certificate: {Path.GetFullPath(TlsFile)}");
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }
}
