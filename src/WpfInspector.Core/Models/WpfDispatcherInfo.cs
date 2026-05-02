namespace WpfInspector.Core.Models;

public sealed record WpfDispatcherInfo(
    int ThreadId,
    string ThreadName,
    string ThreadState,
    string ApartmentState,
    bool HasShutdownStarted,
    bool HasShutdownFinished,
    string CurrentPriority);
