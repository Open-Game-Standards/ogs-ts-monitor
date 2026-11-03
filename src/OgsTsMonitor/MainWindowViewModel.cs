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

    // ---- recording (recording.md) ----

    RecordingWriter? _recorder;
    string? _recordingPath;
    CancellationTokenSource? _playback;

    public bool IsRecording => _recorder is not null;
    public string RecordLabel => IsRecording ? "Stop recording" : "Record";
    public bool IsPlaying => _playback is not null;
    public string PlayLabel => IsPlaying ? "Stop playback" : "Play recording";

    void OnRaw(IPEndPoint source, ReadOnlyMemory<byte> datagram) => _recorder?.Write(source, datagram.Span);

    /// <summary>Starts recording every datagram this monitor receives to <paramref name="path"/>.</summary>
    public void StartRecording(string path)
    {
        if (_recorder is not null) return;
        try
        {
            _recorder = RecordingWriter.Create(path);
            _recordingPath = path;
            Report($"Recording to {Path.GetFileName(path)}.", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Couldn't record to {path}: {ex.Message}", error: true);
        }
        RaiseRecording();
    }

    public void StopRecording()
    {
        if (_recorder is null) return;
        var r = _recorder;
        _recorder = null;
        r.Dispose();
        Report($"Recording saved: {Path.GetFileName(_recordingPath)}.", error: false);
        RaiseRecording();
    }

    /// <summary>Plays a recording into this monitor's views at its original
    /// pace, keeping the recorded senders apart. Nothing is sent on the
    /// network, so no rig moves.</summary>
    public async Task PlayRecordingAsync(string path)
    {
        if (_playback is not null) return;
        List<RecordedDatagram> records;
        try { records = RecordingReader.Read(path).Records; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report($"Couldn't open {Path.GetFileName(path)}: {ex.Message}", error: true);
            return;
        }
        var cts = _playback = new CancellationTokenSource();
        RaisePlayback();
        Report($"Playing {Path.GetFileName(path)} ({records.Count} datagrams) in this monitor only.", error: false);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long first = records.Count > 0 ? records[0].ArrivalMicros : 0;
            foreach (var r in records)
            {
                double wait = (r.ArrivalMicros - first) / 1e6 - clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token);
                _tracker.Ingest(r.Datagram, r.Source);
            }
            Report($"Finished playing {Path.GetFileName(path)}.", error: false);
        }
        catch (OperationCanceledException)
        {
            Report("Playback stopped.", error: false);
        }
        finally
        {
            cts.Dispose();
            _playback = null;
            RaisePlayback();
        }
    }

    public void StopPlayback() => _playback?.Cancel();

    void RaiseRecording() { Raise(nameof(IsRecording)); Raise(nameof(RecordLabel)); }
    void RaisePlayback() { Raise(nameof(IsPlaying)); Raise(nameof(PlayLabel)); }

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
        if (!IPAddress.TryParse(Group, out var group) || !IsMulticast(group))
        {
            Report($"'{Group}' is not a multicast address (IPv4 224.0.0.0 – 239.255.255.255, or IPv6 such as {TelemetrySender.DefaultGroupV6}).", error: true);
            return;
        }
        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            Report($"'{Port}' is not a valid port.", error: true);
            return;
        }
        try
        {
            // the interface list holds IPv4 addresses: an IPv6 group joins on the default interface
            var nic = group.AddressFamily == AddressFamily.InterNetworkV6 ? null : SelectedInterface.Address;
            _receiver = new TelemetryReceiver(Group, port, nic);
            _tracker.Attach(_receiver);
            _receiver.RawDatagramReceived += OnRaw;
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
                _demoReceiver.RawDatagramReceived += OnRaw;
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
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6Multicast;
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
        _playback?.Cancel();
        _recorder?.Dispose();
        _demo?.Dispose();
        _demoReceiver?.Dispose();
        _receiver?.Dispose();
    }
}
