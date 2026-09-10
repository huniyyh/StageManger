using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using StageManager.Core;

namespace StageManager.App;

/// <summary>One card in the strip. Kept alive across refreshes so its container can animate instead of being recreated.</summary>
public sealed class StageItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private BitmapSource? _icon;
    private string _title = "";
    private string _label = "";
    private int _count;

    public Stage Stage { get; }

    public StageItem(Stage stage) => Stage = stage;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public BitmapSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value)) Raise(nameof(InitialVisibility));
        }
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Label
    {
        get => _label;
        set
        {
            if (Set(ref _label, value)) Raise(nameof(Initial));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value)) Raise(nameof(CountVisibility));
        }
    }

    public string Initial => string.IsNullOrEmpty(Label) ? "?" : Label[..1].ToUpperInvariant();
    public Visibility InitialVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CountVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
