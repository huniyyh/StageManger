namespace StageManager.Core;

/// <summary>Distances from the edges of a window rectangle to the part of it that is actually visible.</summary>
public readonly record struct Insets(int Left, int Top, int Right, int Bottom);

/// <summary>
/// A downscaled BGRA32 bitmap of a window, used as its thumbnail while parked. Once the UI has turned the pixels
/// into its own bitmap it calls <see cref="ReleasePixels"/> so the picture is not held twice.
/// </summary>
public sealed class Snapshot
{
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset TakenAt { get; }
    public Insets FrameInsets { get; }

    /// <summary>The pixels, or null after <see cref="ReleasePixels"/>.</summary>
    public byte[]? Bgra { get; private set; }

    public int Stride => Width * 4;

    public Snapshot(int width, int height, byte[] bgra, DateTimeOffset takenAt, Insets frameInsets = default)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
        TakenAt = takenAt;
        FrameInsets = frameInsets;
    }

    /// <summary>Drops the pixel array; the identity of the snapshot (and its size and insets) stays valid.</summary>
    public void ReleasePixels() => Bgra = null;

    /// <summary>The part of <paramref name="windowBounds"/> the pixels cover: the window without its invisible resize borders.</summary>
    public RectPx VisibleArea(RectPx windowBounds) => new(
        windowBounds.Left + FrameInsets.Left,
        windowBounds.Top + FrameInsets.Top,
        windowBounds.Right - FrameInsets.Right,
        windowBounds.Bottom - FrameInsets.Bottom);
}
