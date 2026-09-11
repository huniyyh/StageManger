using System.Collections.ObjectModel;
using System.IO;
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
    private static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan RevealFade = TimeSpan.FromMilliseconds(180); // the pictures dissolve into the real windows over this; short, because a window that changed while parked shows through its old picture
    private static readonly TimeSpan CardSlideDuration = TimeSpan.FromMilliseconds(420); // cards closing a gap or making room, on the same spring as the flights and a little ahead of them
    private static readonly TimeSpan CardFadeDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan StripSlideDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan PeekDwell = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PeekLinger = TimeSpan.FromMilliseconds(400);
    private const int EdgeZonePx = 6;

    private readonly StageEngine _engine;
    private readonly IWindowSystem _ws;
    private readonly Dictionary<WindowId, Picture> _pictures = new();
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

    /// <summary>Stages whose new card stays invisible until the picture flying towards it has landed; see <see cref="RevealHeld"/>.</summary>
    private readonly HashSet<Stage> _held = new();

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

    /// <summary>Brings the cards in line with the engine state, unless an animation sequence is doing that itself.</summary>
    public void Refresh()
    {
        if (!_suppressRefresh) RefreshNow();
    }

    /// <summary>Brings the cards in line with the engine state right now, animating what moved, appeared or disappeared.</summary>
    private void RefreshNow()
    {
        if (!_engine.IsEnabled)
        {
            Hide();
            _overlay?.HideNow();
            Items.Clear();
            _held.Clear();
            _pictures.Clear(); // the engine forgot every window, so their pictures go too
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
        IngestNewSnapshots();
        PrunePictures();
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
    /// where they were, cards that appeared fade and grow in. Cards that vanished simply vanish. Positions are
    /// measured against the strip itself, so the whole group re-centering after a card came or went is part of
    /// the movement rather than a jump.
    /// </summary>
    private void AnimateLayoutChange(Action mutate)
    {
        var before = new Dictionary<StageItem, double>();
        foreach (var item in Items)
            if (ContainerOf(item) is { IsLoaded: true } container)
                before[item] = container.TranslatePoint(new Point(0, 0), StripRoot).Y;

        mutate();

        Dispatcher.InvokeAsync(() =>
        {
            Cards.UpdateLayout();
            foreach (var item in Items)
            {
                if (ContainerOf(item) is not { } container) continue;
                double now = container.TranslatePoint(new Point(0, 0), StripRoot).Y;
                if (before.TryGetValue(item, out double was))
                {
                    double delta = was - now;
                    if (Math.Abs(delta) < 0.5) continue;
                    var slide = new TranslateTransform(0, delta);
                    container.RenderTransform = slide;
                    slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, CardSlideDuration) { EasingFunction = new SpringEase() });
                }
                else if (_held.Contains(item.Stage))
                {
                    container.Opacity = 0; // its place is made now; it shows once the picture bound for it has landed
                }
                else
                {
                    FadeIn(container);
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private static void FadeIn(FrameworkElement container)
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

    /// <summary>Fades in the cards that were kept invisible while a picture flew to them.</summary>
    private void RevealHeld()
    {
        if (_held.Count == 0) return;
        foreach (var item in Items)
            if (_held.Contains(item.Stage) && ContainerOf(item) is { } container) FadeIn(container);
        _held.Clear();
    }

    private StageItem? ItemOf(Stage? stage) => stage == null ? null : Items.FirstOrDefault(it => it.Stage == stage);

    /// <summary>Where a stage's card sits on screen once the pending layout has run; null when it has no card.</summary>
    private RectPx? SlotOf(Stage? stage)
    {
        if (ItemOf(stage) is not { } item) return null;
        Cards.UpdateLayout(); // generates and arranges the container of a card that was just added
        return ScreenRectOf(item);
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

        item.Thumbnail = withSnapshot?.Snapshot is { } snapshot ? CardPicture(withSnapshot.Info.Id, snapshot) : null;
        item.Icon = GetIcon(primary?.Info.ExecutablePath);
        item.Title = primary?.Info.Title ?? stage.Label;
        item.Label = stage.Label;
        item.Count = stage.Windows.Count;
    }

    // ---------------------------------------------------------------- pictures

    /// <summary>
    /// What the strip keeps of one window's snapshot: a card-sized bitmap made right away, and the full picture as a
    /// JPEG once a worker has encoded it. The raw pixels are released at that point, so a parked window costs about a
    /// megabyte rather than the tens of megabytes the raw picture of a large window takes.
    /// </summary>
    private sealed class Picture
    {
        public required Snapshot Snapshot { get; init; }
        public required BitmapSource Card { get; init; }

        /// <summary>The full picture when it is no bigger than a card, so nothing had to be encoded.</summary>
        public BitmapSource? Full { get; init; }

        /// <summary>The full picture, JPEG-encoded; null until the worker is done (the snapshot still holds the pixels then).</summary>
        public byte[]? Jpeg { get; set; }
    }

    private static readonly BitmapSource BlankPicture = ToBitmap(1, 1, new byte[4]);

    private BitmapSource CardPicture(WindowId id, Snapshot snapshot) => Ingest(id, snapshot).Card;

    /// <summary>The picture of a window at the size it flies at: the raw pixels while they are around, the decoded JPEG afterwards.</summary>
    private BitmapSource FlightPicture(WindowId id, Snapshot snapshot)
    {
        var picture = Ingest(id, snapshot);
        if (picture.Full != null) return picture.Full;
        if (snapshot.Bgra is { } pixels) return ToBitmap(snapshot.Width, snapshot.Height, pixels);
        if (picture.Jpeg is { } jpeg)
        {
            try { return DecodeJpeg(jpeg); }
            catch (Exception ex) { Log.Write("picture decoding failed: " + ex.Message); }
        }
        return picture.Card;
    }

    /// <summary>
    /// Takes a snapshot's pixels into the strip's own bitmaps; a snapshot seen before is returned as is. With
    /// <paramref name="jpeg"/> already encoded (by the prefetch worker) nothing is left to do in the background.
    /// </summary>
    private Picture Ingest(WindowId id, Snapshot snapshot, byte[]? jpeg = null)
    {
        if (_pictures.TryGetValue(id, out var existing) && ReferenceEquals(existing.Snapshot, snapshot)) return existing;

        Picture picture;
        if (snapshot.Thumbnail is { } thumbnail)
        {
            var card = thumbnail.Bgra is { } small ? ToBitmap(thumbnail.Width, thumbnail.Height, small) : existing?.Card ?? BlankPicture;
            thumbnail.Release();
            picture = new Picture { Snapshot = snapshot, Card = card, Jpeg = jpeg };
            if (jpeg != null) snapshot.ReleasePixels();
            else if (snapshot.Bgra != null) _ = EncodeAsync(picture);
        }
        else
        {
            // The picture itself fits a card: one bitmap serves both the card and a flight.
            var full = snapshot.Bgra is { } pixels ? ToBitmap(snapshot.Width, snapshot.Height, pixels) : existing?.Full ?? existing?.Card ?? BlankPicture;
            snapshot.ReleasePixels();
            picture = new Picture { Snapshot = snapshot, Card = full, Full = full };
        }
        _pictures[id] = picture;
        return picture;
    }

    private async Task EncodeAsync(Picture picture)
    {
        var snapshot = picture.Snapshot;
        var pixels = snapshot.Bgra!;
        try
        {
            picture.Jpeg = await Task.Run(() => EncodeJpeg(snapshot.Width, snapshot.Height, pixels));
            snapshot.ReleasePixels();
        }
        catch (Exception ex)
        {
            Log.Write("picture encoding failed: " + ex.Message); // the raw pixels stay and serve flights as before
        }
    }

    /// <summary>
    /// Converts snapshots the engine took since the last refresh, including those of windows on other virtual
    /// desktops that no card shows, so no raw picture stays around longer than it takes to encode it.
    /// </summary>
    private void IngestNewSnapshots()
    {
        foreach (var w in _engine.TrackedWindows)
            if (w.Snapshot is { Bgra: not null } snapshot) Ingest(w.Info.Id, snapshot);
    }

    /// <summary>Drops the pictures of windows the engine no longer tracks; without this every window ever closed would keep one alive.</summary>
    private void PrunePictures()
    {
        foreach (var id in _pictures.Keys.Where(id => _engine.GetWindow(id) == null).ToList())
            _pictures.Remove(id);
    }

    private static BitmapSource ToBitmap(int width, int height, byte[] bgra)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>JPEG at quality 90: a screenful of UI compresses to about a megabyte and decodes in tens of milliseconds. Safe on any thread.</summary>
    private static byte[] EncodeJpeg(int width, int height, byte[] bgra)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource DecodeJpeg(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
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
                // The capture and the JPEG encoding both happen on the worker; the UI thread only takes the results.
                var (snapshot, jpeg) = await Task.Run(() =>
                {
                    var s = _ws.CaptureSnapshot(id, StageEngine.SnapshotMaxWidth, StageEngine.SnapshotMaxHeight, StageEngine.ThumbnailMaxWidth, StageEngine.ThumbnailMaxHeight);
                    return (s, s is { Thumbnail: not null, Bgra: { } pixels } ? EncodeJpeg(s.Width, s.Height, pixels) : null);
                });
                if (snapshot == null) continue;
                _engine.StoreSnapshot(id, snapshot); // back on the UI thread; ignored when the window has been parked meanwhile
                if (_engine.GetWindow(id)?.Snapshot == snapshot) Ingest(id, snapshot, jpeg);
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

    /// <summary>A stored picture decoded ahead of a swap, tied to the snapshot it came from.</summary>
    private sealed record WarmPicture(WindowId Id, Snapshot Snapshot, BitmapSource Image);

    private Task<List<WarmPicture>>? _warm;

    /// <summary>
    /// Decodes the stored pictures of a stage on a worker thread while the button is still down, so a click can
    /// start its animation without first spending tens of milliseconds decoding on the UI thread.
    /// </summary>
    private Task<List<WarmPicture>> WarmFlightPicturesAsync(Stage stage)
    {
        var encoded = new List<(WindowId Id, Snapshot Snapshot, byte[] Jpeg)>();
        foreach (var id in stage.Windows)
        {
            if (_engine.GetWindow(id)?.Snapshot is { } snapshot && _pictures.TryGetValue(id, out var picture)
                && ReferenceEquals(picture.Snapshot, snapshot) && picture.Jpeg is { } jpeg)
                encoded.Add((id, snapshot, jpeg));
        }
        if (encoded.Count == 0) return Task.FromResult(new List<WarmPicture>());
        return Task.Run(() => encoded.Select(e => new WarmPicture(e.Id, e.Snapshot, DecodeJpeg(e.Jpeg))).ToList());
    }

    /// <summary>The warmed pictures if they are ready soon; otherwise none, and they are decoded on demand.</summary>
    private async Task<List<WarmPicture>> WarmedAsync()
    {
        var warm = _warm;
        _warm = null;
        if (warm == null) return new List<WarmPicture>();
        await Task.WhenAny(warm, Task.Delay(150));
        if (warm.IsCompletedSuccessfully) return warm.Result;
        if (warm.IsFaulted) Log.Write("warming pictures failed: " + warm.Exception?.GetBaseException().Message);
        return new List<WarmPicture>();
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
        _warm = WarmFlightPicturesAsync(item.Stage); // decoded while the button is down, so a click does not wait for it
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
            var flights = new List<SwapOverlay.Flight> { new(FlightPicture(lead.Id, lead.Snapshot), from.Value, visible) };
            var overlay = Overlay();
            await overlay.PresentAsync(flights, _ws.GetPrimaryWorkArea()); // replaces the ghost with the same picture in the same place
            await overlay.AnimateAsync(SwapDuration);
            await overlay.LandedAsync(); // the picture is visibly in place before the windows appear under it
            _engine.MergeIntoActive(stage, anchor, suppressTransitions: true); // the real windows appear underneath the picture
            RefreshNow(); // the card goes and the others close ranks
            await SwapOverlay.ComposedAsync();
            await overlay.FadeOutAsync(RevealFade); // the picture dissolves into the real window rather than snapping to it
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
            var visible = picture.Snapshot.VisibleArea(picture.Bounds);
            // The picture is bound for the card the strip is about to make for this window. Until that card exists
            // it aims at the strip as a whole, which also makes the overlay cover the whole band.
            var flight = new SwapOverlay.Flight(FlightPicture(id, picture.Snapshot), visible, StripArea());
            var overlay = Overlay();
            await overlay.PresentAsync(new[] { flight }, _ws.GetPrimaryWorkArea());
            _engine.CommitDetach(id, suppressTransitions: true); // the real window vanishes underneath its picture

            var own = _engine.FindStageOf(id);
            if (own != null) _held.Add(own);
            RefreshNow(); // the other cards make room; the new card stays invisible until the picture has landed on it
            flight.To = FitInto(visible, SlotOf(own) ?? StripArea());
            await overlay.AnimateAsync(SwapDuration);
            await overlay.LandedAsync(); // the card appears when the picture is visibly on it
            RevealHeld();
            await overlay.FadeOutAsync(RevealFade);
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
            RevealHeld();
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
        var warm = await WarmedAsync();
        _swapInProgress = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var swap = _engine.PrepareSwap(item.Stage, suppressTransitions: true);
        if (swap == null) return;
        long prepared = clock.ElapsedMilliseconds, shown = 0, parked = 0, animated = 0, landed = 0, presented = 0;

        _swapInProgress = true;
        _suppressRefresh = true;
        _stageLeavingStrip = item.Stage;
        try
        {
            var stripArea = StripArea();
            var cardRect = ScreenRectOf(item) ?? stripArea;

            // Pictures cover only the visible part of a window, so they fly between visible rectangles. The outgoing
            // ones are bound for the card the strip is about to make for their stage; until it exists they aim at
            // the strip as a whole, which also makes the overlay cover the whole band.
            var flights = new List<SwapOverlay.Flight>();
            var outgoing = new List<(SwapOverlay.Flight Flight, RectPx Visible)>();
            foreach (var o in swap.Outgoing)
            {
                if (o.Snapshot == null) continue;
                var visible = o.Snapshot.VisibleArea(o.Bounds);
                var flight = new SwapOverlay.Flight(FlightPicture(o.Id, o.Snapshot), visible, stripArea);
                flights.Add(flight);
                outgoing.Add((flight, visible));
            }
            foreach (var i in swap.Incoming)
            {
                if (i.Snapshot == null) continue;
                var visible = i.Snapshot.VisibleArea(i.Bounds);
                var image = warm.FirstOrDefault(p => p.Id == i.Id && ReferenceEquals(p.Snapshot, i.Snapshot))?.Image ?? FlightPicture(i.Id, i.Snapshot);
                flights.Add(new SwapOverlay.Flight(image, FitInto(visible, cardRect), visible));
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

            // The strip rearranges now: the clicked card goes and the outgoing stage's card appears, invisible until
            // its pictures have landed on it. Where that card ends up is where the pictures go.
            if (swap.From != null) _held.Add(swap.From);
            RefreshNow();
            var slot = SlotOf(swap.From) ?? cardRect;
            foreach (var (flight, visible) in outgoing) flight.To = FitInto(visible, slot);

            await overlay.AnimateAsync(SwapDuration);
            animated = clock.ElapsedMilliseconds;
            await overlay.LandedAsync();   // the pictures are where the windows will be on screen, not just on the clock
            landed = clock.ElapsedMilliseconds;
            _engine.CommitPresent(swap);   // the real incoming windows appear underneath their pictures
            presented = clock.ElapsedMilliseconds;
            await SwapOverlay.ComposedAsync(); // and are on screen before their pictures begin to go
            RevealHeld();                  // the new card fades in while the pictures dissolve
            await overlay.FadeOutAsync(RevealFade);
            Log.Write($"swap timing ms: prepare {prepared}, overlay {shown - prepared}, park {parked - shown}, animate {animated - parked}, land {landed - animated}, present {presented - landed}");
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
            RevealHeld();
            _swapInProgress = false;
            _stageLeavingStrip = null;
            _suppressRefresh = false;
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
