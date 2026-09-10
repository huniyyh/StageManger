using System.Windows;
using System.Windows.Media.Animation;

namespace StageManager.App;

/// <summary>
/// A damped spring: fast at the start, a whisper of overshoot, and a gentle settle. Closer to how macOS moves
/// windows than the polynomial curves WPF ships with. Tuned so it has settled by the end of the animation.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    /// <summary>Damping ratio; below 1 overshoots slightly. 0.78 gives about 2% overshoot.</summary>
    public double Damping { get; set; } = 0.78;

    /// <summary>Angular frequency over the normalized duration; higher settles sooner.</summary>
    public double Frequency { get; set; } = 12.0;

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
