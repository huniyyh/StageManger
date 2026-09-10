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
    Cloaked,
    Uncloaked,
}

public readonly record struct WindowEvent(WindowEventKind Kind, WindowId Window);
