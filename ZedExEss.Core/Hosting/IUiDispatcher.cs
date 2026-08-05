namespace ZedExEss.Hosting;

/// <summary>Relative priorities used when posting work back to the UI thread.</summary>
public enum UiDispatchPriority
{
    Background,
    Normal,
    Loaded,
    Render
}

/// <summary>
/// Marshals callbacks from emulator/audio worker threads without exposing a particular UI
/// toolkit's dispatcher to the application composition code.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    /// <returns><see langword="false"/> when the host dispatcher is shutting down.</returns>
    bool TryPost(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal);
}
