namespace StageManager.Core;

/// <summary>Opaque identity of a top-level window (an HWND on Windows).</summary>
public readonly record struct WindowId(nint Value)
{
    public override string ToString() => $"0x{Value:X}";
}
