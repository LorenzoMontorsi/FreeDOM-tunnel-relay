using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FreeDom.Gui.ViewModels;

namespace FreeDom.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;

        vm.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        vm.Launch = target =>
        {
            if (Directory.Exists(target))
                _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(target));
            else if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                _ = Launcher.LaunchUriAsync(uri);
        };

        // Keep the log scrolled to the newest line.
        vm.LogLines.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || vm.LogLines.Count == 0) return;
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(vm.LogLines.Count - 1), DispatcherPriority.Background);
        };
    }
}
