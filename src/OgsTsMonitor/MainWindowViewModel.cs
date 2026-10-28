using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ogs.Telemetry;
using Ogs.Telemetry.Monitor;

namespace OgsTsMonitor;

/// <summary>A network interface to join the multicast group on; null address = OS default route.</summary>
public sealed record InterfaceChoice(string Label, IPAddress? Address)
{
    public override string ToString() => Label;
}

/// <summary>
/// Owns the receiver (and the optional demo sender) and hands frames to the
/// shared <see cref="StreamTracker"/> that the monitor views present.
/// </summary>
public sealed class MainWindowViewModel : Observable, IDisposable
{
    readonly StreamTracker _tracker = new();
    TelemetryReceiver? _receiver;
    DemoSender? _demo;
    TelemetryReceiver? _demoReceiver;
    /// <summary>The isolated demo's port: not the standard 40123, so no real
    /// receiver is listening on it.</summary>
    public const int DemoPort = 40199;
    readonly bool _demoLan;

    public MainWindowViewModel(Options options)
    {
        Monitor = new MonitorViewModel(_tracker);
        _group = options.Group;
        _port = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Interfaces = DiscoverInterfaces();
        _interface = Interfaces[0];
        _demoLan = options.DemoLan;
        Start();
        if (options.Demo) ToggleDemo();
    }

    public MonitorViewModel Monitor { get; }

    string _group;
    public string Group { get => _group; set => Set(ref _group, value); }

    string _port;
    public string Port { get => _port; set => Set(ref _port, value); }

    public IReadOnlyList<InterfaceChoice> Interfaces { get; }

    InterfaceChoice _interface;
    public InterfaceChoice SelectedInterface { get => _interface; set => Set(ref _interface, value); }

    bool _listening;
    public bool IsListening
    {
        get => _listening;
        private set { if (Set(ref _listening, value)) { Raise(nameof(ListenLabel)); Raise(nameof(CanEditEndpoint)); } }
    }
    public string ListenLabel => IsListening ? "Stop" : "Listen";
    public bool CanEditEndpoint => !IsListening;

    bool _demoRunning;
    public bool DemoRunning
    {
        get => _demoRunning;
        private set { if (Set(ref _demoRunning, value)) Raise(nameof(DemoLabel)); }
    }
    public string DemoLabel => DemoRunning ? "Stop demo sender" : "Start demo sender";

    string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public void ToggleListen()
    {
        if (IsListening) Stop(); else Start();
    }

    void Start()
    {
        if (!IPAddress.TryParse(Group, out var group) || group.AddressFamily != AddressFamily.InterNetwork
            || !IsMulticast(group))
        {
            Report($"'{Group}' is not an IPv4 multicast address (224.0.0.0 – 239.255.255.255).", error: true);
            return;
        }
        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            Report($"'{Port}' is not a valid port.", error: true);
            return;
        }
        try
        {
            _receiver = new TelemetryReceiver(Group, port, SelectedInterface.Address);
            _tracker.Attach(_receiver);
            _receiver.Start();
            IsListening = true;
            Report($"Listening on {Group}:{port} via {SelectedInterface.Label}.", error: false);
        }
        catch (SocketException ex)
        {
            _receiver?.Dispose();
            _receiver = null;
            Report($"Couldn't join {Group}:{port}: {ex.Message}", error: true);
        }
    }

    void Stop()
    {
        _receiver?.Dispose();
        _receiver = null;
        IsListening = false;
        Report("Stopped.", error: false);
    }

    public void ToggleDemo()
    {
        if (_demo is not null)
        {
            _demo.Dispose();
            _demo = null;
            _demoReceiver?.Dispose();
            _demoReceiver = null;
            DemoRunning = false;
            return;
        }
        if (!int.TryParse(Port, out int port)) return;
        try
        {
            if (_demoLan)
            {
                // opt-in: a real sender on the group and port being monitored
                _demo = new DemoSender(Group, port, lan: true);
                Report("Demo sender running on your network: other receivers see it too.", error: false);
            }
            else
            {
                // isolated: loopback only, on its own port, with its own
                // receiver feeding this monitor; a local GameConn on the
                // standard port never sees a demo car
                _demoReceiver ??= new TelemetryReceiver(Group, DemoPort, IPAddress.Loopback);
                _tracker.Attach(_demoReceiver);
                _demoReceiver.Start();
                _demo = new DemoSender(Group, DemoPort, lan: false);
                Report($"Demo sender running on this machine only (loopback, port {DemoPort}).", error: false);
            }
            DemoRunning = true;
        }
        catch (SocketException ex)
        {
            _demo?.Dispose();
            _demo = null;
            Report($"Couldn't start the demo sender: {ex.Message}", error: true);
        }
    }

    public void Clear() => Monitor.Clear();

    void Report(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    static bool IsMulticast(IPAddress a)
    {
        byte first = a.GetAddressBytes()[0];
        return first is >= 224 and <= 239;
    }

    static IReadOnlyList<InterfaceChoice> DiscoverInterfaces()
    {
        var list = new List<InterfaceChoice> { new("default route", null) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(new InterfaceChoice($"{nic.Name} ({ua.Address})", ua.Address));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // enumeration unavailable: the default route still works
        }
        return list;
    }

    public void Dispose()
    {
        _demo?.Dispose();
        _demoReceiver?.Dispose();
        _receiver?.Dispose();
    }
}
