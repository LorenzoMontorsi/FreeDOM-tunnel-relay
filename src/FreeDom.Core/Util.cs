namespace FreeDom.Core;

public static class Log
{
    private static readonly object Gate = new();
    public static bool Verbose { get; set; }

    /// <summary>Set to false in GUI hosts that have no console.</summary>
    public static bool Console { get; set; } = true;

    /// <summary>Every log line (time, level, message), e.g. for a GUI log view. Raised on the logging thread.</summary>
    public static event Action<DateTime, string, string>? Written;

    public static void Debug(string message) { if (Verbose) Write("DBG", message, ConsoleColor.DarkGray); }
    public static void Info(string message) => Write("INF", message, null);
    public static void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor? color)
    {
        var now = DateTime.Now;
        if (Console)
        {
            lock (Gate)
            {
                bool tint = color is not null && !System.Console.IsOutputRedirected;
                if (tint) System.Console.ForegroundColor = color!.Value;
                System.Console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} {level} {message}");
                if (tint) System.Console.ResetColor();
            }
        }
        Written?.Invoke(now, level, message);
    }
}

/// <summary>Minimal "--name value", "--name=value" and "--flag" parser that rejects unknown options.</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public static CliArgs Parse(string[] args, IEnumerable<string> valueOptions, IEnumerable<string> flagOptions)
    {
        var values = new HashSet<string>(valueOptions, StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(flagOptions, StringComparer.OrdinalIgnoreCase) { "help" };
        var result = new CliArgs();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "/?")
            {
                result._flags.Add("help");
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                string name = arg[2..];
                string? inline = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }

                if (flags.Contains(name))
                {
                    if (inline is not null) throw new ArgumentException($"option --{name} does not take a value");
                    result._flags.Add(name);
                }
                else if (values.Contains(name))
                {
                    string value = inline ?? (i + 1 < args.Length
                        ? args[++i]
                        : throw new ArgumentException($"option --{name} requires a value"));
                    result._values[name] = value;
                }
                else
                {
                    throw new ArgumentException($"unknown option --{name}");
                }
            }
            else
            {
                result.Positional.Add(arg);
            }
        }
        return result;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => _flags.Contains(name);

    public int? GetInt(string name)
    {
        string? v = Get(name);
        if (v is null) return null;
        return int.TryParse(v, out int n) ? n : throw new ArgumentException($"option --{name} expects a number, got '{v}'");
    }
}
