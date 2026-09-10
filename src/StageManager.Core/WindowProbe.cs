namespace StageManager.Core;

/// <summary>
/// Raw facts about a top-level window, gathered by the platform layer, from which
/// <see cref="WindowFilter"/> decides whether the window should be managed.
/// </summary>
public sealed record WindowProbe(
    bool IsVisible,
    bool IsChild,
    bool IsToolWindow,
    bool IsAppWindow,
    bool IsNoActivate,
    bool IsCloaked,
    bool IsRootOwner,
    bool IsOwnProcess,
    string Title,
    string ClassName,
    string ProcessName);
