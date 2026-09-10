namespace StageManager.Core;

/// <summary>Distances from the edges of a window rectangle to the part of it that is actually visible.</summary>
public readonly record struct Insets(int Left, int Top, int Right, int Bottom);

/// <summary>A BGRA32 bitmap. <see cref="Release"/> drops the pixels once they have been copied somewhere else.</summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>The pixels, or null after <see cref="Release"/>.</summary>
    public byte[]? Bgra { get; private set; }

    public int Stride => Width * 4;

    public PixelBuffer(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public void Release() => Bgra = null;
}

/// <summary>
/// A BGRA32 picture of a window, taken before it is parked. The full picture is big enough to stand in for the
/// window while it flies across the screen; <see cref="Thumbnail"/> is a small copy for the strip card. Once the
/// UI has turned the pixels into its own bitmaps it releases them so nothing is held twice.
/// </summary>
public sealed class Snapshot
{
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset TakenAt { get; }
    public Insets FrameInsets { get; }

    /// <summary>The pixels of the full picture, or null after <see cref="ReleasePixels"/>.</summary>
    public byte[]? Bgra { get; private set; }

    public int Stride => Width * 4;

    /// <summary>A card-sized copy, or null when the picture itself is already that small.</summary>
    public PixelBuffer? Thumbnail { get; }

    public Snapshot(int width, int height, byte[] bgra, DateTimeOffset takenAt, Insets frameInsets = default, PixelBuffer? thumbnail = null)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
        TakenAt = takenAt;
        FrameInsets = frameInsets;
        Thumbnail = thumbnail;
    }

    /// <summary>Drops the full picture's pixel array; the identity of the snapshot (and its size and insets) stays valid.</summary>
    public void ReleasePixels() => Bgra = null;

    /// <summary>The part of <paramref name="windowBounds"/> the pixels cover: the window without its invisible resize borders.</summary>
    public RectPx VisibleArea(RectPx windowBounds) => new(
        windowBounds.Left + FrameInsets.Left,
        windowBounds.Top + FrameInsets.Top,
        windowBounds.Right - FrameInsets.Right,
        windowBounds.Bottom - FrameInsets.Bottom);
}
