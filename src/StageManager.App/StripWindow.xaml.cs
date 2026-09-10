using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StageManager.Core;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>The strip along one edge of the primary monitor (right by default) that shows one card per parked stage.</summary>
public partial class StripWindow : Window
{
    private const double StripWidthDip = 200;
    private const double MarginDip = 12;
    private const int DragThresholdPx = 6;
    private static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CardSlideDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CardFadeDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan StripSlideDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan PeekDwell = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PeekLinger = TimeSpan.FromMilliseconds(400);
    private const int EdgeZonePx = 6;

    private readonly StageEngine _engine;
    private readonly IWindowSystem _ws;
    private readonly Dictionary<WindowId, (Snapshot Snapshot, BitmapSource Bitmap)> _thumbnails = new();
    private readonly Dictionary<string, BitmapSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _pressTimer;
    private readonly DispatcherTimer _windowDragTimer;
    private readonly DispatcherTimer _peekTimer;
    private bool _swapInProgress;
    private bool _suppressRefresh;
    private bool _slidOut;
    private bool _peeking;
    private DateTime _edgeSince = DateTime.MinValue;
    private DateTime _leftSince = DateTime.MinValue;
    private Stage? _stageLeavingStrip;
    private SwapOverlay? _overlay;
    private CardPress? _press;

    public ObservableCollection<StageItem> Items { get; } = new();

    public StripWindow(StageEngine engine, IWindowSystem ws)
    {
        _engine = engine;
        _ws = ws;
        InitializeComponent();
        DataContext = this;

        _pressTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _pressTimer.Tick += OnPressTick;
        _windowDragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _windowDragTimer.Tick += OnWindowDragTick;
        _peekTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
        _peekTimer.Tick += OnPeekTick;
        _engine.UserDragChanged += OnUserDragChanged;
        _engine.DroppedOnStrip += OnWindowDroppedOnStrip;

        Log.Write("visuals: " + Visuals.Description);
        if (Visuals.SoftwareRendering)
        {
            // Without a GPU every blur is computed on the CPU for every frame; the cards look fine without them.
            Resources["ThumbShadow"] = null;
            Resources["TextShadow"] = null;
        }

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

    // ---------------------------------------------------------------- cards

    /// <summary>Brings the cards in line with the engine state, animating what moved, appeared or disappeared.</summary>
    public void Refresh()
    {
        if (_suppressRefresh) return;
        if (!_engine.IsEnabled)
        {
            Hide();
            _overlay?.HideNow();
            Items.Clear();
            _thumbnails.Clear(); // the engine forgot every window, so their pictures go too
            _peekTimer.Stop();
            _peeking = false;
            _slidOut = false;
            StripSlide.BeginAnimation(TranslateTransform.XProperty, null);
            StripSlide.X = 0;
            StripRoot.IsHitTestVisible = true;
            return;
        }

        PositionOnPrimaryMonitor();
        var desired = _engine.StripStages.Where(s => s != _stageLeavingStrip).ToList(); // a leaving stage's picture is mid-flight
        AnimateLayoutChange(() =>
        {
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!desired.Contains(Items[i].Stage)) Items.RemoveAt(i);

            for (int i = 0; i < desired.Count; i++)
            {
                var item = Items.FirstOrDefault(it => it.Stage == desired[i]);
                if (item == null)
                {
                    item = new StageItem(desired[i]);
                    Items.Insert(i, item);
                }
                else if (Items.IndexOf(item) != i)
                {
                    Items.Move(Items.IndexOf(item), i);
                }
                Populate(item);
            }
        });
        PruneThumbnails();
        if (!IsVisible) Show();
        UpdateCoverage();
    }

    // ---------------------------------------------------------------- getting out of the way

    /// <summary>
    /// When the active stage covers the strip's area (a maximized window, or one dragged over it) the strip slides
    /// off the screen edge, like macOS hides its strip when a window needs the space. Resting the pointer on the
    /// edge peeks it back in until the pointer leaves.
    /// </summary>
    private void UpdateCoverage()
    {
        bool covered = _engine.IsStripCovered;
        if (covered)
        {
            if (!_peekTimer.IsEnabled) _peekTimer.Start();
        }
        else
        {
            _peekTimer.Stop();
            _peeking = false;
            _edgeSince = DateTime.MinValue;
            _leftSince = DateTime.MinValue;
        }
        Slide(hidden: covered && !_peeking);
    }

    private void Slide(bool hidden)
    {
        if (_slidOut == hidden) return;
        _slidOut = hidden;
        StripRoot.IsHitTestVisible = !hidden;
        double to = hidden ? StripWidthDip + 16 : 0;
        StripSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, StripSlideDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnPeekTick(object? sender, EventArgs e)
    {
        if (_swapInProgress || _press != null)
        {
            _leftSince = DateTime.MinValue;
            return;
        }

        var cursor = NativeWindow.GetCursorPosition();
        var area = StripArea();
        var now = DateTime.UtcNow;
        bool inStrip = area.Contains(cursor);
        bool atEdge = cursor.X >= area.Right - EdgeZonePx && cursor.Y >= area.Top && cursor.Y < area.Bottom;

        if (!_peeking)
        {
            if (!atEdge)
            {
                _edgeSince = DateTime.MinValue;
                return;
            }
            if (_edgeSince == DateTime.MinValue)
            {
                _edgeSince = now;
                return;
            }
            if (now - _edgeSince < PeekDwell) return; // a deliberate rest on the edge, not a pass-through
            _peeking = true;
            _leftSince = DateTime.MinValue;
            Slide(hidden: false);
            return;
        }

        if (inStrip)
        {
            _leftSince = DateTime.MinValue;
            return;
        }
        if (_leftSince == DateTime.MinValue)
        {
            _leftSince = now;
            return;
        }
        if (now - _leftSince < PeekLinger) return;
        _peeking = false;
        _edgeSince = DateTime.MinValue;
        Slide(hidden: true);
    }

    /// <summary>
    /// Runs a change to <see cref="Items"/> and animates its effect the FLIP way: cards that moved slide from
    /// where they were, cards that appeared fade and grow in. Cards that vanished simply vanish.
    /// </summary>
    private void AnimateLayoutChange(Action mutate)
    {
        var before = new Dictionary<StageItem, double>();
        foreach (var item in Items)
            if (ContainerOf(item) is { IsLoaded: true } container)
                before[item] = container.TranslatePoint(new Point(0, 0), Cards).Y;

        mutate();

        Dispatcher.InvokeAsync(() =>
        {
            Cards.UpdateLayout();
            foreach (var item in Items)
            {
                if (ContainerOf(item) is not { } container) continue;
                double now = container.TranslatePoint(new Point(0, 0), Cards).Y;
                if (before.TryGetValue(item, out double was))
                {
                    double delta = was - now;
                    if (Math.Abs(delta) < 0.5) continue;
                    var slide = new TranslateTransform(0, delta);
                    container.RenderTransform = slide;
                    slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, CardSlideDuration) { EasingFunction = new SpringEase() });
                }
                else
                {
                    container.Opacity = 0;
                    container.RenderTransformOrigin = new Point(0.5, 0.5);
                    var grow = new ScaleTransform(0.88, 0.88);
                    container.RenderTransform = grow;
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    container.BeginAnimation(OpacityProperty, new DoubleAnimation(1, CardFadeDuration));
                    grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, CardFadeDuration) { EasingFunction = ease });
                    grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, CardFadeDuration) { EasingFunction = ease });
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private FrameworkElement? ContainerOf(StageItem item)
        => Cards.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    private void PositionOnPrimaryMonitor()
    {
        var area = StripArea();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = area.Left / scale;
        Top = area.Top / scale;
        Width = StripWidthDip;
        Height = area.Height / scale;
    }

    private RectPx StripArea() => StageLayout.StripArea(_ws.GetPrimaryWorkArea(), _engine.Layout);

    private void Populate(StageItem item)
    {
        var stage = item.Stage;
        var primaryId = stage.Primary ?? stage.Windows[0];
        var primary = _engine.GetWindow(primaryId) ?? _engine.GetWindow(stage.Windows[0]);
        var withSnapshot = stage.Windows.Select(_engine.GetWindow).FirstOrDefault(w => w?.Snapshot != null);

        item.Thumbnail = withSnapshot?.Snapshot is { } snapshot ? GetThumbnail(withSnapshot.Info.Id, snapshot) : null;
        item.Icon = GetIcon(primary?.Info.ExecutablePath);
        item.Title = primary?.Info.Title ?? stage.Label;
        item.Label = stage.Label;
        item.Count = stage.Windows.Count;
    }

    private BitmapSource GetThumbnail(WindowId id, Snapshot snapshot)
    {
        if (_thumbnails.TryGetValue(id, out var cached) && ReferenceEquals(cached.Snapshot, snapshot))
            return cached.Bitmap;
        if (snapshot.Bgra == null)
            return cached.Bitmap ?? BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4); // pixels already handed over

        var bitmap = BitmapSource.Create(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgr32, null, snapshot.Bgra, snapshot.Stride);
        bitmap.Freeze();
        snapshot.ReleasePixels(); // the bitmap now owns the only copy
        _thumbnails[id] = (snapshot, bitmap);
        return bitmap;
    }

    /// <summary>
    /// Drops the pictures of windows the engine no longer tracks. Each is up to a couple of megabytes, and without
    /// this every window ever closed would keep one alive for the life of the process.
    /// </summary>
    private void PruneThumbnails()
    {
        foreach (var id in _thumbnails.Keys.Where(id => _engine.GetWindow(id) == null).ToList())
            _thumbnails.Remove(id);
    }

    private BitmapSource? GetIcon(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        if (_icons.TryGetValue(executablePath, out var cached)) return cached;

        BitmapSource? result = null;
        nint icon = NotificationIcon.ExtractFileIcon(executablePath);
        if (icon != 0)
        {
            try
            {
                result = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                result.Freeze();
            }
            catch (Exception ex)
            {
                Log.Write($"icon conversion failed for {executablePath}: {ex.Message}");
            }
            finally
            {
                NotificationIcon.DestroyIcon(icon);
            }
        }
        _icons[executablePath] = result;
        return result;
    }

    private SwapOverlay Overlay() => _overlay ??= new SwapOverlay();

    // The pointer arriving over the strip usually means a click is coming: take the outgoing pictures now and
    // bring the overlay up, so the swap itself waits for neither.
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_swapInProgress || !_engine.IsEnabled) return;
        _ = PrefetchAsync(TimeSpan.FromMilliseconds(300));
        Overlay().EnsureVisible(_ws.GetPrimaryWorkArea());
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_swapInProgress && _press == null) _overlay?.ReleaseSoon();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_swapInProgress) _ = PrefetchAsync(TimeSpan.FromSeconds(1));
    }

    private readonly HashSet<WindowId> _capturing = new();
    private Task _prefetch = Task.CompletedTask;

    /// <summary>
    /// Refreshes stale pictures of the active stage on a worker thread. Rendering a big window takes tens of
    /// milliseconds, which would otherwise stall the UI thread every time the pointer crosses the strip.
    /// </summary>
    private Task PrefetchAsync(TimeSpan maxAge)
    {
        var stale = _engine.SnapshotsToRefresh(maxAge).Where(_capturing.Add).ToList();
        if (stale.Count == 0) return _prefetch;
        _prefetch = CaptureAllAsync(stale);
        return _prefetch;
    }

    private async Task CaptureAllAsync(List<WindowId> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                var snapshot = await Task.Run(() => _ws.CaptureSnapshot(id, StageEngine.SnapshotMaxWidth, StageEngine.SnapshotMaxHeight));
                if (snapshot != null) _engine.StoreSnapshot(id, snapshot); // back on the UI thread
            }
            catch (Exception ex)
            {
                Log.Write("prefetch failed: " + ex.Message);
            }
            finally
            {
                _capturing.Remove(id);
            }
        }
    }

    /// <summary>Gives an in-flight prefetch a moment to finish so the swap can reuse its picture instead of taking another.</summary>
    private async Task AwaitPrefetchAsync()
    {
        if (_prefetch.IsCompleted) return;
        await Task.WhenAny(_prefetch, Task.Delay(250));
    }

    // ---------------------------------------------------------------- pressing and dragging cards

    /// <summary>A card the user is holding down: becomes a click on release, or a drag once the pointer moves.</summary>
    private sealed class CardPress
    {
        public required StageItem Item { get; init; }
        public required FrameworkElement Card { get; init; }
        public required PointPx Start { get; init; }
        public required RectPx ThumbRect { get; init; }
        public bool Dragging { get; set; }
    }

    /// <summary>
    /// The strip never activates, so it cannot capture the mouse and stops hearing about it once the pointer
    /// leaves. Instead the press is followed with a timer that reads the pointer and button state directly.
    /// </summary>
    private void OnCardPressed(object sender, MouseButtonEventArgs e)
    {
        if (_swapInProgress || _press != null) return;
        if (sender is not FrameworkElement card || card.DataContext is not StageItem item) return;
        e.Handled = true;

        if (NativeWindow.IsShiftDown())
        {
            // As on macOS: Shift-click adds the stage to the current one instead of swapping.
            _ = MergeWithAnimationAsync(item.Stage, anchor: null, from: ScreenRectOf(item));
            return;
        }

        var cursor = NativeWindow.GetCursorPosition();
        _press = new CardPress
        {
            Item = item,
            Card = card,
            Start = cursor,
            ThumbRect = ScreenRectOf(item) ?? RectPx.FromSize(cursor.X - 80, cursor.Y - 50, 160, 100),
        };
        _pressTimer.Start();
    }

    private void OnPressTick(object? sender, EventArgs e)
    {
        if (_press is not { } press)
        {
            _pressTimer.Stop();
            return;
        }

        var cursor = NativeWindow.GetCursorPosition();
        bool buttonDown = NativeWindow.IsLeftButtonDown();

        if (!press.Dragging
            && (Math.Abs(cursor.X - press.Start.X) > DragThresholdPx || Math.Abs(cursor.Y - press.Start.Y) > DragThresholdPx))
        {
            press.Dragging = true;
            Log.Write($"card drag begins: {press.Item.Stage}");
            press.Card.Opacity = 0.35;
            if (press.Item.Thumbnail != null)
            {
                Overlay().ShowGhost(press.Item.Thumbnail, GhostRect(press, cursor), _ws.GetPrimaryWorkArea());
            }
        }
        if (press.Dragging) _overlay?.MoveGhost(GhostRect(press, cursor));
        if (buttonDown) return;

        // Released.
        _pressTimer.Stop();
        _press = null;
        press.Card.Opacity = 1;
        if (!press.Dragging)
        {
            _ = SwapToAsync(press.Item);
            return;
        }

        bool backOnStrip = StripArea().Contains(cursor);
        Log.Write($"card dropped at {cursor}: {(backOnStrip ? "back on the strip, cancelled" : "merge " + press.Item.Stage)}");
        if (backOnStrip)
        {
            _overlay?.HideGhost();
            _overlay?.ReleaseSoon();
            return;
        }
        _ = MergeWithAnimationAsync(press.Item.Stage, cursor, GhostRect(press, cursor));
    }

    private static RectPx GhostRect(CardPress press, PointPx cursor)
        => RectPx.FromSize(
            press.ThumbRect.Left + (cursor.X - press.Start.X),
            press.ThumbRect.Top + (cursor.Y - press.Start.Y),
            press.ThumbRect.Width,
            press.ThumbRect.Height);

    /// <summary>Merges a stage into the active one; its picture grows from <paramref name="from"/> to where the window will be.</summary>
    private async Task MergeWithAnimationAsync(Stage stage, PointPx? anchor, RectPx? from)
    {
        var plan = _engine.PlanMerge(stage, anchor);
        var lead = plan.FirstOrDefault(p => p.IsPrimary && p.Snapshot != null) ?? plan.FirstOrDefault(p => p.Snapshot != null);
        if (lead?.Snapshot == null || from == null || _swapInProgress)
        {
            _overlay?.HideGhost();
            _overlay?.ReleaseSoon();
            _engine.MergeIntoActive(stage, anchor, suppressTransitions: true);
            return;
        }

        _swapInProgress = true;
        _suppressRefresh = true;
        try
        {
            var visible = lead.Snapshot.VisibleArea(lead.Bounds);
            var flights = new List<SwapOverlay.Flight> { new(GetThumbnail(lead.Id, lead.Snapshot), from.Value, visible) };
            var overlay = Overlay();
            await overlay.PresentAsync(flights, _ws.GetPrimaryWorkArea()); // replaces the ghost with the same picture in the same place
            await overlay.AnimateAsync(SwapDuration);
            _engine.MergeIntoActive(stage, anchor, suppressTransitions: true); // the real windows appear underneath the picture
            _suppressRefresh = false;
            Refresh();
            await Task.Delay(RevealDelay);
        }
        catch (Exception ex)
        {
            Log.Write("merge animation failed: " + ex);
            _engine.MergeIntoActive(stage, anchor, suppressTransitions: true);
        }
        finally
        {
            _overlay?.Dismiss();
            _overlay?.ReleaseSoon();
            _swapInProgress = false;
            _suppressRefresh = false;
            Refresh();
        }
    }

    // ---------------------------------------------------------------- windows dragged onto the strip

    private void OnUserDragChanged(bool dragging)
    {
        if (dragging)
        {
            _windowDragTimer.Start();
            Overlay().EnsureVisible(_ws.GetPrimaryWorkArea()); // ready in case the drag ends on the strip
            return;
        }
        _windowDragTimer.Stop();
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!_swapInProgress) _overlay?.ReleaseSoon();
    }

    private void OnWindowDragTick(object? sender, EventArgs e)
    {
        bool over = StripArea().Contains(NativeWindow.GetCursorPosition());
        DropHighlight.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
        if (over && _slidOut)
        {
            _peeking = true; // a window being dragged onto a hidden strip brings it out to receive the drop
            Slide(hidden: false);
        }
    }

    /// <summary>The user dropped a stage window on the strip: its picture shrinks into the top slot while the window is parked.</summary>
    private async void OnWindowDroppedOnStrip(WindowId id)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        var picture = _engine.PrepareDetach(id);
        if (picture?.Snapshot == null || _swapInProgress)
        {
            _engine.CommitDetach(id, suppressTransitions: picture != null);
            return;
        }

        _swapInProgress = true;
        _suppressRefresh = true;
        try
        {
            var topSlot = (Items.Count > 0 ? ScreenRectOf(Items[0]) : null) ?? StripArea();
            var visible = picture.Snapshot.VisibleArea(picture.Bounds);
            var flights = new List<SwapOverlay.Flight>
            {
                new(GetThumbnail(id, picture.Snapshot), visible, FitInto(visible, topSlot)),
            };

            var overlay = Overlay();
            await overlay.PresentAsync(flights, _ws.GetPrimaryWorkArea());
            _engine.CommitDetach(id, suppressTransitions: true); // the real window vanishes underneath its picture
            await overlay.AnimateAsync(SwapDuration);
            _suppressRefresh = false;
            Refresh(); // the new card fades in underneath the picture
            await Task.Delay(RevealDelay);
        }
        catch (Exception ex)
        {
            Log.Write("detach animation failed: " + ex);
            _engine.CommitDetach(id);
        }
        finally
        {
            _overlay?.Dismiss();
            _overlay?.ReleaseSoon();
            _swapInProgress = false;
            _suppressRefresh = false;
            Refresh();
        }
    }

    // ---------------------------------------------------------------- swapping with animation

    /// <summary>
    /// Switches to the clicked stage. Pictures of the outgoing windows shrink into the top slot of the strip while the
    /// picture of the incoming stage grows out of its card; the real windows change underneath the overlay.
    /// </summary>
    private async Task SwapToAsync(StageItem item)
    {
        if (_swapInProgress) return;
        _swapInProgress = true; // reserve the slot while we wait; released below or in finally
        await AwaitPrefetchAsync();
        _swapInProgress = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var swap = _engine.PrepareSwap(item.Stage, suppressTransitions: true);
        if (swap == null) return;
        long prepared = clock.ElapsedMilliseconds, shown = 0, parked = 0, animated = 0, presented = 0;

        _swapInProgress = true;
        _stageLeavingStrip = item.Stage;
        try
        {
            var stripArea = StripArea();
            var cardRect = ScreenRectOf(item) ?? stripArea;
            var topSlot = (Items.Count > 0 ? ScreenRectOf(Items[0]) : null) ?? cardRect;
            AnimateLayoutChange(() => Items.Remove(item)); // the cards below slide up into the gap

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

            var overlay = Overlay();
            await overlay.PresentAsync(flights, _ws.GetPrimaryWorkArea());
            shown = clock.ElapsedMilliseconds;
            _engine.CommitPark(swap);      // the real outgoing windows vanish underneath their pictures
            parked = clock.ElapsedMilliseconds;
            await overlay.AnimateAsync(SwapDuration);
            animated = clock.ElapsedMilliseconds;
            _engine.CommitPresent(swap);   // the real incoming windows appear underneath their pictures; the new card fades in
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
            _overlay?.ReleaseSoon();
            _swapInProgress = false;
            _stageLeavingStrip = null;
            Refresh();
        }
    }

    /// <summary>Screen rectangle, in physical pixels, of a card's thumbnail box.</summary>
    private RectPx? ScreenRectOf(StageItem item)
    {
        if (ContainerOf(item) is not { } container) return null;
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
