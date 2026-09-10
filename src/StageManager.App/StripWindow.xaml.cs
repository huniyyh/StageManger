using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StageManager.Core;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>The strip along one edge of the primary monitor (right by default) that shows one card per parked stage.</summary>
public partial class StripWindow : Window
{
    private const double StripWidthDip = 200;
    private const double MarginDip = 12;

    private readonly StageEngine _engine;
    private readonly IWindowSystem _ws;
    private readonly Dictionary<WindowId, (Snapshot Snapshot, BitmapSource Bitmap)> _thumbnails = new();
    private readonly Dictionary<string, BitmapSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<StageItem> Items { get; } = new();

    public StripWindow(StageEngine engine, IWindowSystem ws)
    {
        _engine = engine;
        _ws = ws;
        InitializeComponent();
        DataContext = this;

        // Create the HWND now so the layout settings are correct before the engine is first enabled.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindow.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);
        UpdateLayoutSettings();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateLayoutSettings();
    }

    private void UpdateLayoutSettings()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _engine.Layout = _engine.Layout with
        {
            StripWidth = (int)Math.Round(StripWidthDip * scale),
            Margin = (int)Math.Round(MarginDip * scale),
        };
    }

    /// <summary>Rebuilds the cards from the engine state and shows or hides the strip.</summary>
    public void Refresh()
    {
        if (!_engine.IsEnabled)
        {
            Hide();
            Items.Clear();
            return;
        }

        PositionOnPrimaryMonitor();
        Items.Clear();
        foreach (var stage in _engine.StripStages)
            Items.Add(BuildItem(stage));
        if (!IsVisible) Show();
    }

    private void PositionOnPrimaryMonitor()
    {
        var area = StageLayout.StripArea(_ws.GetPrimaryWorkArea(), _engine.Layout);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = area.Left / scale;
        Top = area.Top / scale;
        Width = StripWidthDip;
        Height = area.Height / scale;
    }

    private StageItem BuildItem(Stage stage)
    {
        var primaryId = stage.Primary ?? stage.Windows[0];
        var primary = _engine.GetWindow(primaryId) ?? _engine.GetWindow(stage.Windows[0]);
        var withSnapshot = stage.Windows.Select(_engine.GetWindow).FirstOrDefault(w => w?.Snapshot != null);

        BitmapSource? thumbnail = null;
        if (withSnapshot?.Snapshot is { } snapshot)
            thumbnail = GetThumbnail(withSnapshot.Info.Id, snapshot);

        var icon = GetIcon(primary?.Info.ExecutablePath);
        var title = primary?.Info.Title ?? stage.Label;
        return new StageItem(stage, thumbnail, icon, title, stage.Label, stage.Windows.Count);
    }

    private BitmapSource GetThumbnail(WindowId id, Snapshot snapshot)
    {
        if (_thumbnails.TryGetValue(id, out var cached) && ReferenceEquals(cached.Snapshot, snapshot))
            return cached.Bitmap;

        var bitmap = BitmapSource.Create(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgr32, null, snapshot.Bgra, snapshot.Stride);
        bitmap.Freeze();
        _thumbnails[id] = (snapshot, bitmap);
        return bitmap;
    }

    private BitmapSource? GetIcon(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        if (_icons.TryGetValue(executablePath, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (icon != null)
            {
                result = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                result.Freeze();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"icon extraction failed for {executablePath}: {ex.Message}");
        }
        _icons[executablePath] = result;
        return result;
    }

    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StageItem item)
            _engine.ActivateStage(item.Stage);
    }
}
