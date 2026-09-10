using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
    private static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(90);

    private readonly StageEngine _engine;
    private readonly IWindowSystem _ws;
    private readonly Dictionary<WindowId, (Snapshot Snapshot, BitmapSource Bitmap)> _thumbnails = new();
    private readonly Dictionary<string, BitmapSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private bool _swapInProgress;
    private Stage? _stageLeavingStrip;
    private SwapOverlay? _overlay;

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
            _overlay?.Hide();
            Items.Clear();
            return;
        }

        PositionOnPrimaryMonitor();
        Items.Clear();
        foreach (var stage in _engine.StripStages)
        {
            if (stage == _stageLeavingStrip) continue; // its picture is mid-flight
            Items.Add(BuildItem(stage));
        }
        if (!IsVisible) Show();

        _overlay ??= new SwapOverlay();
        _overlay.EnsureVisible(_ws.GetPrimaryWorkArea());
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

    // ---------------------------------------------------------------- swapping with animation

    // The pointer arriving over the strip usually means a click is coming: take the outgoing pictures now,
    // so the swap itself does not have to wait for a screen capture.
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_swapInProgress) _engine.PrefetchActiveSnapshots(TimeSpan.FromMilliseconds(300));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_swapInProgress) _engine.PrefetchActiveSnapshots(TimeSpan.FromSeconds(1));
    }

    private async void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (_swapInProgress) return;
        if ((sender as FrameworkElement)?.DataContext is not StageItem item) return;
        await SwapToAsync(item);
    }

    /// <summary>
    /// Switches to the clicked stage. Pictures of the outgoing windows shrink into the top slot of the strip while the
    /// picture of the incoming stage grows out of its card; the real windows change underneath the overlay.
    /// </summary>
    private async Task SwapToAsync(StageItem item)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var swap = _engine.PrepareSwap(item.Stage, suppressTransitions: true);
        if (swap == null) return;
        long prepared = clock.ElapsedMilliseconds, shown = 0, parked = 0, animated = 0, presented = 0;

        _swapInProgress = true;
        _stageLeavingStrip = item.Stage;
        try
        {
            var stripArea = StageLayout.StripArea(_ws.GetPrimaryWorkArea(), _engine.Layout);
            var cardRect = ScreenRectOf(item) ?? stripArea;
            var topSlot = (Items.Count > 0 ? ScreenRectOf(Items[0]) : null) ?? cardRect;
            Items.Remove(item);

            // Pictures cover only the visible part of a window, so they fly between visible rectangles.
            var flights = new List<SwapOverlay.Flight>();
            foreach (var o in swap.Outgoing)
            {
                if (o.Snapshot == null) continue;
                var visible = o.Snapshot.VisibleArea(o.Bounds);
                flights.Add(new SwapOverlay.Flight(GetThumbnail(o.Id, o.Snapshot), visible, FitInto(visible, topSlot)));
            }
            foreach (var i in swap.Incoming)
            {
                if (i.Snapshot == null) continue;
                var visible = i.Snapshot.VisibleArea(i.Bounds);
                flights.Add(new SwapOverlay.Flight(GetThumbnail(i.Id, i.Snapshot), FitInto(visible, cardRect), visible));
            }

            if (flights.Count == 0)
            {
                _engine.CommitPresent(swap);
                return;
            }

            _overlay ??= new SwapOverlay();
            _overlay.EnsureVisible(_ws.GetPrimaryWorkArea());
            await _overlay.PresentAsync(flights);
            shown = clock.ElapsedMilliseconds;
            _engine.CommitPark(swap);      // the real outgoing windows vanish underneath their pictures
            parked = clock.ElapsedMilliseconds;
            await _overlay.AnimateAsync(SwapDuration);
            animated = clock.ElapsedMilliseconds;
            _engine.CommitPresent(swap);   // the real incoming windows appear underneath their pictures
            presented = clock.ElapsedMilliseconds;
            await Task.Delay(RevealDelay); // give them a moment to paint before the pictures go away
            Log.Write($"swap timing ms: prepare {prepared}, overlay {shown - prepared}, park {parked - shown}, animate {animated - parked}, present {presented - animated}");
        }
        catch (Exception ex)
        {
            Log.Write("swap animation failed: " + ex);
            _engine.CommitPresent(swap);
        }
        finally
        {
            _overlay?.Dismiss();
            _swapInProgress = false;
            _stageLeavingStrip = null;
            Refresh();
        }
    }

    /// <summary>Screen rectangle, in physical pixels, of a card's thumbnail box.</summary>
    private RectPx? ScreenRectOf(StageItem item)
    {
        if (Cards.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) return null;
        var box = FindDescendant<Border>(container, "ThumbBox");
        if (box == null || box.ActualWidth <= 0 || box.ActualHeight <= 0) return null;
        var topLeft = box.PointToScreen(new Point(0, 0));
        var bottomRight = box.PointToScreen(new Point(box.ActualWidth, box.ActualHeight));
        return new RectPx((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y);
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>The rectangle a picture with the aspect ratio of <paramref name="source"/> fills when shown uniformly inside <paramref name="slot"/>.</summary>
    private static RectPx FitInto(RectPx source, RectPx slot)
    {
        if (source.Width <= 0 || source.Height <= 0 || slot.Width <= 0 || slot.Height <= 0) return slot;
        double scale = Math.Min((double)slot.Width / source.Width, (double)slot.Height / source.Height);
        int width = Math.Max(1, (int)(source.Width * scale));
        int height = Math.Max(1, (int)(source.Height * scale));
        return RectPx.FromSize(slot.Left + (slot.Width - width) / 2, slot.Top + (slot.Height - height) / 2, width, height);
    }
}
