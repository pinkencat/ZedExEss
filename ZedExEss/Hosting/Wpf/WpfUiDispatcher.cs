using System.Windows.Threading;
using ZedExEss.Hosting;

namespace ZedExEss.Hosting.Wpf;

/// <summary>Maps portable dispatch requests onto a WPF dispatcher.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public bool TryPost(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return false;
        }

        _dispatcher.BeginInvoke(MapPriority(priority), action);
        return true;
    }

    private static DispatcherPriority MapPriority(UiDispatchPriority priority) => priority switch
    {
        UiDispatchPriority.Background => DispatcherPriority.Background,
        UiDispatchPriority.Loaded => DispatcherPriority.Loaded,
        UiDispatchPriority.Render => DispatcherPriority.Render,
        _ => DispatcherPriority.Normal
    };
}
