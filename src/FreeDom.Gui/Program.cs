using Avalonia;

namespace FreeDom.Gui;

sealed class Program
{
    /// <summary>"--minimized" starts hidden in the tray (useful for launching at login).</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>"--config path" uses a specific config file (portable setups, multiple profiles).</summary>
    public static string? ConfigPath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        int i = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length) ConfigPath = Path.GetFullPath(args[i + 1]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
