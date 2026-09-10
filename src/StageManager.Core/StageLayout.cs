namespace StageManager.Core;

/// <summary>Which edge of the work area the strip occupies.</summary>
public enum StripSide
{
    Left,
    Right,
}

/// <summary>Layout constants in physical pixels.</summary>
public sealed record LayoutSettings(int StripWidth = 200, int Margin = 12, StripSide Side = StripSide.Right);

public static class StageLayout
{
    /// <summary>The full-height band along the work area edge where the strip is drawn.</summary>
    public static RectPx StripArea(RectPx workArea, LayoutSettings s)
        => s.Side == StripSide.Left
            ? new RectPx(workArea.Left, workArea.Top, workArea.Left + s.StripWidth, workArea.Bottom)
            : new RectPx(workArea.Right - s.StripWidth, workArea.Top, workArea.Right, workArea.Bottom);

    /// <summary>The region beside the strip where stage windows may live.</summary>
    public static RectPx AvailableArea(RectPx workArea, LayoutSettings s)
        => s.Side == StripSide.Left
            ? new RectPx(workArea.Left + s.StripWidth + s.Margin, workArea.Top + s.Margin, workArea.Right - s.Margin, workArea.Bottom - s.Margin)
            : new RectPx(workArea.Left + s.Margin, workArea.Top + s.Margin, workArea.Right - s.StripWidth - s.Margin, workArea.Bottom - s.Margin);

    /// <summary>Fits a window into the available area: shrinks it if needed, then centers it or clamps its position.</summary>
    public static RectPx Place(RectPx window, RectPx available, bool center)
    {
        if (available.Width <= 0 || available.Height <= 0) return window;

        int w = Math.Min(window.Width, available.Width);
        int h = Math.Min(window.Height, available.Height);
        int left, top;
        if (center)
        {
            left = available.Left + (available.Width - w) / 2;
            top = available.Top + (available.Height - h) / 2;
        }
        else
        {
            left = Math.Clamp(window.Left, available.Left, available.Right - w);
            top = Math.Clamp(window.Top, available.Top, available.Bottom - h);
        }
        return RectPx.FromSize(left, top, w, h);
    }
}
