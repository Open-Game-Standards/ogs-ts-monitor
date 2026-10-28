using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Ogs.Telemetry.Monitor;

/// <summary>The monitor views. Set <see cref="StyledElement.DataContext"/> to a
/// <see cref="MonitorViewModel"/>; while attached, the view refreshes it at
/// ~30 Hz and keeps the stream log scrolled to the newest line unless the
/// user has scrolled up.</summary>
public partial class MonitorView : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    MonitorViewModel? _vm;
    ScrollViewer? _logScroll;
    bool _followLog = true;

    public MonitorView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => _vm?.Refresh();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = DataContext as MonitorViewModel;
        if (_vm is not null) _vm.LogAppended += OnLogAppended;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    void OnLogAppended()
    {
        _logScroll ??= LogList.FindDescendantOfType<ScrollViewer>();
        if (_logScroll is not null)
        {
            // follow only while the user is at (or near) the bottom
            double bottom = _logScroll.Extent.Height - _logScroll.Viewport.Height;
            _followLog = bottom <= 0 || _logScroll.Offset.Y >= bottom - 24;
        }
        if (_followLog && LogList.ItemCount > 0)
            LogList.ScrollIntoView(LogList.ItemCount - 1);
    }
}
