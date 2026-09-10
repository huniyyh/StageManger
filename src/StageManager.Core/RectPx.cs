namespace StageManager.Core;

/// <summary>A point in physical pixels.</summary>
public readonly record struct PointPx(int X, int Y);

/// <summary>Rectangle in physical pixels using the Win32 convention (Right and Bottom are exclusive).</summary>
public readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public PointPx Center => new(Left + Width / 2, Top + Height / 2);

    public static RectPx FromSize(int left, int top, int width, int height)
        => new(left, top, left + width, top + height);

    /// <summary>A rectangle of the same size whose center is <paramref name="center"/>.</summary>
    public RectPx CenteredAt(PointPx center)
        => FromSize(center.X - Width / 2, center.Y - Height / 2, Width, Height);

    public bool Contains(PointPx p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool IntersectsWith(RectPx o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}
