using System.Windows;
using System.Windows.Media.Animation;

namespace PodsView;

/// <summary>
/// 0.8.46: a low reading blinks, so a raised card or window says at a glance which of the
/// three channels caused it. Only opacity moves; colour and layout stay put.
/// </summary>
internal static class LowBatteryPulse
{
    private static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "LowBatteryPulseActive", typeof(bool), typeof(LowBatteryPulse), new PropertyMetadata(false));

    internal static void Set(bool on, params UIElement?[] elements)
    {
        foreach (UIElement? element in elements)
        {
            if (element is null) continue;
            bool active = (bool)element.GetValue(ActiveProperty);
            if (on == active) continue;
            element.SetValue(ActiveProperty, on);
            if (on)
            {
                var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(520))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                element.BeginAnimation(UIElement.OpacityProperty, blink);
            }
            else
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
            }
        }
    }
}
