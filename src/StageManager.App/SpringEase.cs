using System.Windows;
using System.Windows.Media.Animation;

namespace StageManager.App;

/// <summary>
/// A damped spring: quick to get going, then decelerating visibly for the rest of the animation. Closer to how
/// macOS moves windows than the polynomial curves WPF ships with. The defaults are shared by the flying pictures
/// and the strip's cards: 35% of the way at a fifth of the duration, 84% at half, 95% at 70%, settled at the end.
/// A stiffer spring (say damping 0.78, frequency 12) arrives within a third of the duration and then sits still,
/// which reads as abrupt.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    /// <summary>Damping ratio; below 1 overshoots. 0.92 stays just short of any visible overshoot.</summary>
    public double Damping { get; set; } = 0.92;

    /// <summary>Angular frequency over the normalized duration; higher settles sooner. Below about 6 the curve is not settled at the end.</summary>
    public double Frequency { get; set; } = 6.0;

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn; // EaseInCore is the whole response curve; do not mirror it
    }

    protected override double EaseInCore(double t)
    {
        double zeta = Math.Clamp(Damping, 0.05, 0.999);
        double omega = Frequency;
        double damped = omega * Math.Sqrt(1 - zeta * zeta);
        double decay = Math.Exp(-zeta * omega * t);
        return 1 - decay * (Math.Cos(damped * t) + zeta * omega / damped * Math.Sin(damped * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
