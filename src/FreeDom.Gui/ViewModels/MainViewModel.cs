using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDom.ClientCore;
using FreeDom.Core;

namespace FreeDom.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));

    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(Server), nameof(Token), nameof(Fingerprint), nameof(UseTls), nameof(Insecure),
        nameof(AutoConnect), nameof(MinimizeToTray),
    ];

    private readonly DispatcherTimer _saveTimer;
    private readonly bool _loading;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private ClientState _lastState = ClientState.Stopped;
    private Dictionary<string, TunnelRowViewModel> _runningRows = [];

    public MainViewModel() : this(ClientConfig.DefaultUserPath) { }

    public MainViewModel(string configPath)
    {
        ConfigPath = configPath;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };

        Tunnels.CollectionChanged += OnTunnelsChanged;
        Log.Written += OnLogWritten;

        _loading = true;
        Load();
        _loading = false;

        if (AutoConnect) Dispatcher.UIThread.Post(Start);
    }

    public string ConfigPath { get; }
    public string ConfigHint => $"Configurazione salvata automaticamente in {ConfigPath}";

    // ------------------------------------------------------------- persisted settings

    [ObservableProperty] public partial string Server { get; set; } = "";
    [ObservableProperty] public partial string Token { get; set; } = "";
    [ObservableProperty] public partial string Fingerprint { get; set; } = "";
    [ObservableProperty] public partial bool UseTls { get; set; } = true;
    [ObservableProperty] public partial bool Insecure { get; set; }
    [ObservableProperty] public partial bool AutoConnect { get; set; }
    [ObservableProperty] public partial bool MinimizeToTray { get; set; } = !OperatingSystem.IsLinux();

    public ObservableCollection<TunnelRowViewModel> Tunnels { get; } = [];

    // ------------------------------------------------------------- runtime state

    [ObservableProperty] public partial bool ShowToken { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(StartStopText))]
    public partial bool IsRunning { get; set; }

    public bool CanEdit => !IsRunning;
    public string StartStopText => IsRunning ? "■  Ferma" : "▶  Avvia";

    [ObservableProperty] public partial string StatusText { get; set; } = "Fermo";
    [ObservableProperty] public partial string? StatusDetail { get; set; }
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Gray;
    [ObservableProperty] public partial int ActiveConnections { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingFingerprint))]
    public partial string? PendingFingerprint { get; set; }

    [ObservableProperty] public partial string PendingFingerprintTitle { get; set; } = "";
    [ObservableProperty] public partial string PendingFingerprintText { get; set; } = "";
    public bool HasPendingFingerprint => PendingFingerprint is not null;

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>Platform services provided by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }
    public Action<string>? Launch { get; set; }

    // ------------------------------------------------------------- persistence

    private void Load()
    {
        ClientConfig config;
        try
        {
            config = File.Exists(ConfigPath) ? ClientConfig.Load(ConfigPath) : new ClientConfig();
        }
        catch (Exception ex)
        {
            AppendLog(DateTime.Now, "ERR", $"Impossibile leggere {ConfigPath}: {ex.Message}");
            config = new ClientConfig();
        }

        Server = config.Server ?? "";
        Token = config.Token ?? "";
        Fingerprint = config.Fingerprint ?? "";
        UseTls = config.Tls ?? true;
        Insecure = config.Insecure;
        AutoConnect = config.AutoConnect;
        MinimizeToTray = config.MinimizeToTray ?? !OperatingSystem.IsLinux();

        foreach (var t in config.Tunnels) Tunnels.Add(TunnelRowViewModel.FromModel(this, t));
        if (Tunnels.Count == 0)
            Tunnels.Add(new TunnelRowViewModel(this) { Name = "web", Type = "http", Local = "3000" });
    }

    private ClientConfig BuildConfig(bool strict) => new()
    {
        Server = string.IsNullOrWhiteSpace(Server) ? null : Server.Trim(),
        Token = string.IsNullOrEmpty(Token) ? null : Token,
        Fingerprint = string.IsNullOrWhiteSpace(Fingerprint) ? null : Fingerprint.Trim(),
        Tls = UseTls ? null : false,
        Insecure = Insecure,
        AutoConnect = AutoConnect,
        MinimizeToTray = MinimizeToTray,
        Tunnels = Tunnels.Select(t => strict ? t.ToModel() : t.ToModelForSave()).ToList(),
    };

    public void Save()
    {
        _saveTimer.Stop();
        try
        {
            BuildConfig(strict: false).Save(ConfigPath);
        }
        catch (Exception ex)
        {
            AppendLog(DateTime.Now, "ERR", $"Salvataggio configurazione fallito: {ex.Message}");
        }
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && PersistedProperties.Contains(e.PropertyName)) ScheduleSave();
    }

    private void OnTunnelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (TunnelRowViewModel row in e.NewItems) row.PropertyChanged += OnRowChanged;
        if (e.OldItems is not null)
            foreach (TunnelRowViewModel row in e.OldItems) row.PropertyChanged -= OnRowChanged;
        ScheduleSave();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!TunnelRowViewModel.IsRuntimeProperty(e.PropertyName)) ScheduleSave();
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private void AddTunnel()
    {
        string name = "web";
        for (int i = 2; Tunnels.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"web{i}";
        Tunnels.Add(new TunnelRowViewModel(this) { Name = name, Type = "http", Local = "" });
    }

    public void RemoveTunnel(TunnelRowViewModel row)
    {
        if (!IsRunning) Tunnels.Remove(row);
    }

    [RelayCommand]
    private async Task StartStop()
    {
        if (IsRunning) await StopAsync();
        else Start();
    }

    [RelayCommand]
    private void TrustFingerprint()
    {
        if (PendingFingerprint is null) return;
        Fingerprint = PendingFingerprint;
        PendingFingerprint = null;
        Save();
        Start();
    }

    [RelayCommand]
    private void DismissFingerprint() => PendingFingerprint = null;

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private void OpenConfigFolder()
    {
        Save();
        if (Path.GetDirectoryName(ConfigPath) is { } dir) Launch?.Invoke(dir);
    }

    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || CopyToClipboard is null) return;
        await CopyToClipboard(text);
        AppendLog(DateTime.Now, "INF", $"Copiato negli appunti: {text}");
    }

    public void Open(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Launch?.Invoke(url);
    }

    // ------------------------------------------------------------- tunnel lifecycle

    public void Start()
    {
        if (IsRunning) return;
        Save();

        ClientSettings settings;
        try
        {
            settings = BuildConfig(strict: true).ToSettings();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            SetStatus("Configurazione non valida", Red, ex.Message);
            AppendLog(DateTime.Now, "ERR", ex.Message);
            return;
        }

        // ToSettings keeps enabled tunnels in order, so names map back to rows by position.
        var enabledRows = Tunnels.Where(t => t.Enabled).ToList();
        _runningRows = settings.Tunnels.Select((t, i) => (t.Name, Row: enabledRows[i]))
            .ToDictionary(x => x.Name, x => x.Row, StringComparer.OrdinalIgnoreCase);

        PendingFingerprint = null;
        var client = new TunnelClient(settings);
        client.StateChanged += (state, detail) => Dispatcher.UIThread.Post(() => OnClientState(state, detail));
        client.TunnelsOnline += list => Dispatcher.UIThread.Post(() => OnTunnelsOnline(list));
        client.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ActiveConnections = Math.Max(0, n));
        client.CertificateRejected += ex => Dispatcher.UIThread.Post(() => OnCertificateRejected(ex));

        foreach (var row in Tunnels) row.IsEditable = false;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runTask = Task.Run(() => client.RunAsync(token));
        _runTask.ContinueWith(_ => Dispatcher.UIThread.Post(OnStopped), TaskScheduler.Default);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask is { } task)
        {
            try { await task; } catch { }
        }
    }

    private void OnStopped()
    {
        _cts?.Dispose();
        _cts = null;
        _runTask = null;
        IsRunning = false;
        ActiveConnections = 0;
        foreach (var row in Tunnels)
        {
            row.IsEditable = true;
            row.PublicUrl = null;
            row.LocalTarget = null;
        }
        if (_lastState != ClientState.Failed) SetStatus("Fermo", Gray, null);
    }

    private void OnClientState(ClientState state, string? detail)
    {
        _lastState = state;
        switch (state)
        {
            case ClientState.Connecting: SetStatus("Connessione…", Amber, null); break;
            case ClientState.Online: SetStatus("Online", Green, null); break;
            case ClientState.Reconnecting:
                SetStatus("Riconnessione…", Amber, detail);
                foreach (var row in Tunnels) row.PublicUrl = null;
                break;
            case ClientState.Failed:
                SetStatus("Errore", Red, PendingFingerprint is not null ? "certificato del server da confermare" : detail);
                break;
            case ClientState.Stopped: SetStatus("Fermo", Gray, null); break;
        }
    }

    private void OnTunnelsOnline(IReadOnlyList<ActiveTunnel> tunnels)
    {
        foreach (var t in tunnels)
        {
            if (!_runningRows.TryGetValue(t.Name, out var row)) continue;
            row.PublicUrl = t.PublicUrl;
            row.LocalTarget = $"→ {t.Local}";
        }
    }

    private void OnCertificateRejected(CertificateRejectedException ex)
    {
        PendingFingerprintTitle = ex.Mismatch
            ? "⚠ Il certificato del server è CAMBIATO"
            : "Primo collegamento a questo server";
        PendingFingerprintText = ex.Mismatch
            ? "Il server presenta un fingerprint diverso da quello salvato. Se non hai rigenerato il certificato sul server, " +
              "qualcuno potrebbe intercettare la connessione. Fidati solo se corrisponde a quello nel log del server:"
            : "Il server usa un certificato auto-firmato. Verifica che il fingerprint qui sotto corrisponda a quello " +
              "stampato nel log del server (journalctl -u freedom-server), poi conferma:";
        PendingFingerprint = ex.Fingerprint;
    }

    private void SetStatus(string text, IBrush brush, string? detail)
    {
        StatusText = text;
        StatusBrush = brush;
        StatusDetail = detail;
    }

    // ------------------------------------------------------------- log

    private void OnLogWritten(DateTime time, string level, string message) =>
        Dispatcher.UIThread.Post(() => AppendLog(time, level, message));

    private void AppendLog(DateTime time, string level, string message)
    {
        LogLines.Add($"{time:HH:mm:ss}  {level}  {message}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }
}
