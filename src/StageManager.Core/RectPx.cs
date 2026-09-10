namespace StageManager.Core;

/// <summary>Rectangle in physical pixels using the Win32 convention (Right and Bottom are exclusive).</summary>
public readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static RectPx FromSize(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}
