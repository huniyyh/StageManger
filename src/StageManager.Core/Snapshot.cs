namespace StageManager.Core;

/// <summary>A downscaled BGRA32 bitmap of a window, used as its thumbnail while parked.</summary>
public sealed record Snapshot(int Width, int Height, byte[] Bgra, DateTimeOffset TakenAt)
{
    public int Stride => Width * 4;
}
