using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StageManager.Core;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>
/// A transparent, click-through window covering the work area on which window snapshots fly between the stage
/// and the strip. The real windows are minimized and restored underneath it, so all the user sees is the pictures
/// moving. It stays open (and empty) while Stage Manager is enabled so a swap does not pay for showing a window.
/// </summary>
internal sealed class SwapOverlay : Window
{
    /// <summary>A picture that travels from one screen rectangle to another, both in physical pixels.</summary>
    public sealed record Flight(BitmapSource Image, RectPx From, RectPx To);

    private const double CornerRadiusDip = 8;

    private readonly Canvas _canvas = new();
    private readonly List<(FrameworkElement Element, Flight Flight)> _flights = new();
    private FrameworkElement? _ghost;
    private RectPx _area;
    private double _scale = 1;

    public SwapOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _canvas;
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        NativeWindow.MakeNoActivateToolWindow(handle);
        NativeWindow.MakeClickThrough(handle);
    }

    /// <summary>Makes sure the (empty) overlay covers <paramref name="area"/> and is showing.</summary>
    public void EnsureVisible(RectPx area)
    {
        _area = area;
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = area.Left / _scale;
        Top = area.Top / _scale;
        Width = area.Width / _scale;
        Height = area.Height / _scale;
        if (!IsVisible) Show();
    }

    /// <summary>Puts every flight at its start rectangle; completes once that frame is on screen.</summary>
    public async Task PresentAsync(IReadOnlyList<Flight> flights)
    {
        EnsureVisible(_area);
        _canvas.Children.Clear();
        _flights.Clear();
        _ghost = null;
        foreach (var flight in flights)
        {
            var picture = MakePicture(flight.Image, opacity: 1);
            Place(picture, flight.From);
            _canvas.Children.Add(picture);
            _flights.Add((picture, flight));
        }
        NativeWindow.RaiseTopmost(new WindowInteropHelper(this).Handle); // above the strip

        // Loaded priority runs after the pending layout and render pass; the extra frame lets the
        // composition thread push the layered bitmap to the screen before anything underneath changes.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(16);
    }

    /// <summary>Moves every flight to its target rectangle along a spring curve.</summary>
    public Task AnimateAsync(TimeSpan duration)
    {
        var done = new TaskCompletionSource();
        if (_flights.Count == 0)
        {
            done.TrySetResult();
            return done.Task;
        }

        var storyboard = new Storyboard();
        var easing = new SpringEase();
        foreach (var (element, flight) in _flights)
        {
            var (left, top, width, height) = ToDip(flight.To);
            storyboard.Children.Add(Animate(element, Canvas.LeftProperty, left, duration, easing));
            storyboard.Children.Add(Animate(element, Canvas.TopProperty, top, duration, easing));
            storyboard.Children.Add(Animate(element, WidthProperty, width, duration, easing));
            storyboard.Children.Add(Animate(element, HeightProperty, height, duration, easing));
        }
        storyboard.Completed += (_, _) => done.TrySetResult();
        storyboard.Begin(this);
        return Task.WhenAny(done.Task, Task.Delay(duration + TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>Removes the pictures; the overlay stays open and invisible.</summary>
    public void Dismiss()
    {
        _canvas.Children.Clear();
        _flights.Clear();
        _ghost = null;
    }

    // ---------------------------------------------------------------- drag ghost

    /// <summary>Shows a picture that follows the pointer while a card is being dragged out of the strip.</summary>
    public void ShowGhost(BitmapSource image, RectPx rect)
    {
        EnsureVisible(_area);
        HideGhost();
        _ghost = MakePicture(image, opacity: 0.94);
        Place(_ghost, rect);
        _canvas.Children.Add(_ghost);
        NativeWindow.RaiseTopmost(new WindowInteropHelper(this).Handle);
    }

    public void MoveGhost(RectPx rect)
    {
        if (_ghost != null) Place(_ghost, rect);
    }

    public void HideGhost()
    {
        if (_ghost == null) return;
        _canvas.Children.Remove(_ghost);
        _ghost = null;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A window picture drawn like a window: rounded corners and, with a GPU to pay for it, a soft shadow.</summary>
    private static FrameworkElement MakePicture(BitmapSource image, double opacity)
    {
        var picture = new Border
        {
            CornerRadius = new CornerRadius(CornerRadiusDip),
            Background = new ImageBrush(image) { Stretch = Stretch.Fill },
            Opacity = opacity,
            Effect = Visuals.SoftwareRendering ? null : new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.38 },
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Linear);
        return picture;
    }

    private static DoubleAnimation Animate(FrameworkElement target, DependencyProperty property, double to, TimeSpan duration, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(to, duration) { EasingFunction = easing };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }

    private void Place(FrameworkElement picture, RectPx rect)
    {
        var (left, top, width, height) = ToDip(rect);
        Canvas.SetLeft(picture, left);
        Canvas.SetTop(picture, top);
        picture.Width = width;
        picture.Height = height;
    }

    private (double Left, double Top, double Width, double Height) ToDip(RectPx rect)
        => ((rect.Left - _area.Left) / _scale, (rect.Top - _area.Top) / _scale, rect.Width / _scale, rect.Height / _scale);
}
