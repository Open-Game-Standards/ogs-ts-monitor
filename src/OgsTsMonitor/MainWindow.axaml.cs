using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace OgsTsMonitor;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    void OnListen(object? sender, RoutedEventArgs e) => Vm?.ToggleListen();
    void OnClear(object? sender, RoutedEventArgs e) => Vm?.Clear();
    void OnDemo(object? sender, RoutedEventArgs e) => Vm?.ToggleDemo();

    static readonly FilePickerFileType RecordingType = new("OGS-TS recording") { Patterns = new[] { "*.ogsrec" } };

    async void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsRecording) { vm.StopRecording(); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Record the OGS-TS stream",
            DefaultExtension = "ogsrec",
            SuggestedFileName = $"ogs-ts-{DateTime.Now:yyyyMMdd-HHmmss}.ogsrec",
            FileTypeChoices = new[] { RecordingType },
        });
        if (file?.TryGetLocalPath() is { } path) vm.StartRecording(path);
    }

    async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsPlaying) { vm.StopPlayback(); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Play an OGS-TS recording in this monitor",
            AllowMultiple = false,
            FileTypeFilter = new[] { RecordingType },
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) await vm.PlayRecordingAsync(path);
    }
}
