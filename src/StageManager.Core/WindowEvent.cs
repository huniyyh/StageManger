namespace StageManager.Core;

public enum WindowEventKind
{
    Foreground,
    Shown,
    Hidden,
    Destroyed,
    TitleChanged,
    MinimizeStarted,
    MinimizeEnded,
    MoveSizeStarted,
    MoveSizeEnded,
    /// <summary>Position, size or state changed, including maximize and restore. Frequent; handled lazily.</summary>
    LocationChanged,
    Cloaked,
    Uncloaked,
}

public readonly record struct WindowEvent(WindowEventKind Kind, WindowId Window);
