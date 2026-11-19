using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FreeDom.Gui.ViewModels;
using FreeDom.Gui.Views;

namespace FreeDom.Gui;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _toggleItem;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _vm = Program.ConfigPath is { } path ? new MainViewModel(path) : new MainViewModel();
            _window = new MainWindow { DataContext = _vm };
            _window.Closing += OnWindowClosing;
            CreateTray();

            bool hidden = Program.StartMinimized && _vm.MinimizeToTray;
            if (!hidden) desktop.MainWindow = _window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray()
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FreeDOM/Assets/freedom.png")));

        var show = new NativeMenuItem("Mostra FreeDOM");
        show.Click += (_, _) => ShowWindow();
        _toggleItem = new NativeMenuItem("Avvia");
        _toggleItem.Click += (_, _) => _vm!.StartStopCommand.Execute(null);
        var quit = new NativeMenuItem("Esci");
        quit.Click += async (_, _) => await QuitAsync();

        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _tray = new TrayIcon { Icon = icon, ToolTipText = "FreeDOM - fermo", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);

        _vm!.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(MainViewModel.IsRunning) && _toggleItem is not null)
            _toggleItem.Header = _vm.IsRunning ? "Ferma" : "Avvia";
        if (e.PropertyName is nameof(MainViewModel.StatusText) && _tray is not null)
            _tray.ToolTipText = $"FreeDOM - {_vm.StatusText}";
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        if (_vm?.MinimizeToTray == true) _window?.Hide();
        else _ = QuitAsync();
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        if (_vm is not null)
        {
            _vm.Save();
            await _vm.StopAsync();
        }
        if (_tray is not null) _tray.IsVisible = false;
        _window?.Close();
        _desktop?.Shutdown();
    }
}
