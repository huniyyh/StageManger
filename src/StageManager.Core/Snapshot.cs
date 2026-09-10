namespace StageManager.Core;

/// <summary>Distances from the edges of a window rectangle to the part of it that is actually visible.</summary>
public readonly record struct Insets(int Left, int Top, int Right, int Bottom);

/// <summary>A downscaled BGRA32 bitmap of a window, used as its thumbnail while parked.</summary>
public sealed record Snapshot(int Width, int Height, byte[] Bgra, DateTimeOffset TakenAt, Insets FrameInsets = default)
{
    public int Stride => Width * 4;

    /// <summary>The part of <paramref name="windowBounds"/> the pixels cover: the window without its invisible resize borders.</summary>
    public RectPx VisibleArea(RectPx windowBounds) => new(
        windowBounds.Left + FrameInsets.Left,
        windowBounds.Top + FrameInsets.Top,
        windowBounds.Right - FrameInsets.Right,
        windowBounds.Bottom - FrameInsets.Bottom);
}
