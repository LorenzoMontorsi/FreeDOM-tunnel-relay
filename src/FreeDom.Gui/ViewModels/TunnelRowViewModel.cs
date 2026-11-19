using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeDom.ClientCore;

namespace FreeDom.Gui.ViewModels;

public partial class TunnelRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public static string[] Types { get; } = ["http", "tcp", "udp"];

    public TunnelRowViewModel(MainViewModel owner) => _owner = owner;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHttp), nameof(UsesPort))]
    public partial string Type { get; set; } = "http";

    [ObservableProperty] public partial string Local { get; set; } = "";
    [ObservableProperty] public partial string Subdomain { get; set; } = "";
    [ObservableProperty] public partial string Hostname { get; set; } = "";
    [ObservableProperty] public partial string RemotePort { get; set; } = "";

    // Runtime state (not persisted).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublicUrl))]
    public partial string? PublicUrl { get; set; }

    [ObservableProperty] public partial string? LocalTarget { get; set; }
    [ObservableProperty] public partial bool IsEditable { get; set; } = true;

    public bool IsHttp => Type == "http";
    /// <summary>tcp and udp tunnels are exposed on a public port.</summary>
    public bool UsesPort => Type is "tcp" or "udp";
    public bool HasPublicUrl => !string.IsNullOrEmpty(PublicUrl);

    /// <summary>Property names that are runtime-only and must not trigger a config save.</summary>
    public static bool IsRuntimeProperty(string? name) =>
        name is nameof(PublicUrl) or nameof(LocalTarget) or nameof(IsEditable) or nameof(HasPublicUrl)
            or nameof(IsHttp) or nameof(UsesPort);

    [RelayCommand] private void Remove() => _owner.RemoveTunnel(this);
    [RelayCommand] private Task Copy() => _owner.CopyAsync(PublicUrl);
    [RelayCommand] private void Open() => _owner.Open(PublicUrl);

    public static TunnelRowViewModel FromModel(MainViewModel owner, ClientTunnel t) => new(owner)
    {
        Enabled = !t.Disabled,
        Name = t.Name,
        Type = t.Type is "tcp" or "udp" ? t.Type : "http",
        Local = t.Local,
        Subdomain = t.Subdomain ?? "",
        Hostname = t.Hostname ?? "",
        RemotePort = t.RemotePort > 0 ? t.RemotePort.ToString() : "",
    };

    public ClientTunnel ToModel()
    {
        int port = 0;
        if (!string.IsNullOrWhiteSpace(RemotePort) && (!int.TryParse(RemotePort.Trim(), out port) || port is < 1 or > 65535))
            throw new ArgumentException($"tunnel '{Name}': porta pubblica non valida '{RemotePort}'");
        return new ClientTunnel
        {
            Disabled = !Enabled,
            Name = Name.Trim(),
            Type = Type,
            Local = Local.Trim(),
            Subdomain = string.IsNullOrWhiteSpace(Subdomain) ? null : Subdomain.Trim(),
            Hostname = string.IsNullOrWhiteSpace(Hostname) ? null : Hostname.Trim(),
            RemotePort = port,
        };
    }

    /// <summary>Lenient variant used for saving: never throws, keeps whatever was typed.</summary>
    public ClientTunnel ToModelForSave()
    {
        try { return ToModel(); }
        catch (ArgumentException)
        {
            return new ClientTunnel
            {
                Disabled = !Enabled, Name = Name, Type = Type, Local = Local,
                Subdomain = string.IsNullOrWhiteSpace(Subdomain) ? null : Subdomain,
                Hostname = string.IsNullOrWhiteSpace(Hostname) ? null : Hostname,
            };
        }
    }
}
