using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OgsTsMonitor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Options.Parse(desktop.Args ?? Array.Empty<string>());
            var vm = new MainWindowViewModel(options);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Command line: <c>--group 239.255.17.17 --port 40123 --demo [--demo-lan]</c>.</summary>
public sealed record Options(string Group, int Port, bool Demo, bool DemoLan)
{
    public static Options Parse(string[] args)
    {
        string group = "239.255.17.17";
        int port = 40123;
        bool demo = false, demoLan = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--group" when i + 1 < args.Length: group = args[++i]; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--demo": demo = true; break;
                case "--demo-lan": demo = demoLan = true; break;
            }
        }
        return new Options(group, port, demo, demoLan);
    }
}
