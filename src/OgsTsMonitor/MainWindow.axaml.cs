using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OgsTsMonitor;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    void OnListen(object? sender, RoutedEventArgs e) => Vm?.ToggleListen();
    void OnClear(object? sender, RoutedEventArgs e) => Vm?.Clear();
    void OnDemo(object? sender, RoutedEventArgs e) => Vm?.ToggleDemo();
}
