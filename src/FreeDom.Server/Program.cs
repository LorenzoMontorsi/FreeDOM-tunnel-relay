using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FreeDom.Core;
using FreeDom.Server;

ServerOptions? options;
try
{
    options = ServerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}\n\nRun 'freedom-server --help' for usage.");
    return 2;
}
if (options is null) return 0;

Log.Verbose = options.Verbose;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

try
{
    var certificate = options.LoadCertificate();
    if (certificate is not null)
        Log.Info($"Control certificate SHA-256 fingerprint: {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
    else
        Log.Warn("Control channel TLS is OFF: tokens and tunnel traffic travel in clear between client and server.");
    if (options.NoAuth)
        Log.Warn("Authentication is disabled (--no-auth): anyone can open tunnels on this server.");

    await new TunnelServer(options, certificate).RunAsync(cts.Token);
    Log.Info("Server stopped.");
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Error(ex.Message);
    return 1;
}
