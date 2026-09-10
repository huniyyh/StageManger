using System.Windows;
using System.Windows.Media.Imaging;
using StageManager.Core;

namespace StageManager.App;

/// <summary>One card in the strip.</summary>
public sealed record StageItem(Stage Stage, BitmapSource? Thumbnail, BitmapSource? Icon, string Title, string Label, int Count)
{
    public string Initial => string.IsNullOrEmpty(Label) ? "?" : Label[..1].ToUpperInvariant();
    public Visibility InitialVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CountVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;
}
