using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PodsView;

public partial class MainWindow
{
    private bool? _refinedApplied;
    private readonly List<Action> _restoreAppearance = new();
    internal bool RefinedForVerification => _refinedApplied == true;

    private void OverrideAppearance(DependencyObject target, DependencyProperty property, object value)
    {
        object original = target.ReadLocalValue(property);
        _restoreAppearance.Add(() =>
        {
            if (original == DependencyProperty.UnsetValue) target.ClearValue(property);
            else target.SetValue(property, original);
        });
        target.SetValue(property, value);
    }

    private void ApplyAppearance()
    {
        if (_productPlayer is null) return;
        bool refined = ThemeManager.IsRefined;
        if (_refinedApplied == refined) return;
        foreach (Action restore in _restoreAppearance) restore();
        _restoreAppearance.Clear();
        _refinedApplied = refined;
        if (refined)
        {
            var font = new FontFamily("Segoe UI, Arial");
            OverrideAppearance(AppearanceFrame, Border.CornerRadiusProperty, new CornerRadius(12));
            OverrideAppearance(CompactRoot, FrameworkElement.MarginProperty, new Thickness(22,20,22,14));
            OverrideAppearance(CompactRoot.RowDefinitions[0], RowDefinition.HeightProperty, new GridLength(42));
            OverrideAppearance(CompactRoot.RowDefinitions[1], RowDefinition.HeightProperty, new GridLength(143));
            OverrideAppearance(AppearanceBody, FrameworkElement.MarginProperty, new Thickness(0,11,0,0));
            OverrideAppearance(AppearanceBody.ColumnDefinitions[1], ColumnDefinition.WidthProperty, new GridLength(20));
            OverrideAppearance(AppearanceProductTile, Border.PaddingProperty, new Thickness(7));
            OverrideAppearance(CompactDeviceNameText, TextBlock.FontFamilyProperty, font);
            OverrideAppearance(CompactDeviceNameText, TextBlock.FontSizeProperty, 19d);
            OverrideAppearance(CompactDeviceNameText, TextBlock.FontWeightProperty, FontWeights.SemiBold);
            OverrideAppearance(CompactDeviceNameText, TextBlock.LineHeightProperty, 23d);
            OverrideAppearance(CompactConnectionText, TextBlock.FontFamilyProperty, font);
            OverrideAppearance(CompactConnectionText, TextBlock.FontWeightProperty, FontWeights.Normal);
            OverrideAppearance(CompactConnectionDot, FrameworkElement.WidthProperty, 5d);
            OverrideAppearance(CompactConnectionDot, FrameworkElement.HeightProperty, 5d);
            OverrideAppearance(CompactConnectionDot, FrameworkElement.MarginProperty, new Thickness(0,0,6,0));
            foreach (Grid row in new[] { AppearanceLeftRow, AppearanceRightRow, AppearanceCaseRow })
            {
                OverrideAppearance(row.ColumnDefinitions[0], ColumnDefinition.WidthProperty, new GridLength(22));
                OverrideAppearance(row.ColumnDefinitions[3], ColumnDefinition.WidthProperty, new GridLength(75));
            }
            foreach (Border tag in new[] { AppearanceLeftTag, AppearanceRightTag, AppearanceCaseTag })
            {
                OverrideAppearance(tag, Border.BorderThicknessProperty, new Thickness(0));
                OverrideAppearance(tag, Border.PaddingProperty, new Thickness(0));
                OverrideAppearance(tag, Border.BackgroundProperty, Brushes.Transparent);
            }
            foreach (TextBlock label in new[] { CompactLeftLabel, CompactRightLabel, CompactCaseLabel })
            {
                OverrideAppearance(label, TextBlock.FontFamilyProperty, font);
                OverrideAppearance(label, TextBlock.FontSizeProperty, 12d);
                OverrideAppearance(label, TextBlock.FontWeightProperty, FontWeights.Normal);
                OverrideAppearance(label, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            }
            foreach (TextBlock value in new[] { CompactLeftValueText, CompactRightValueText, CompactCaseValueText })
            {
                OverrideAppearance(value, TextBlock.FontFamilyProperty, font);
                OverrideAppearance(value, TextBlock.FontWeightProperty, FontWeights.SemiBold);
            }
            foreach (Border track in new[] { AppearanceLeftTrack, AppearanceRightTrack, AppearanceCaseTrack })
                OverrideAppearance(track, FrameworkElement.HeightProperty, 3d);
            if (VersionLabel.Parent is Border version)
            {
                OverrideAppearance(version, Border.BorderThicknessProperty, new Thickness(0));
                OverrideAppearance(version, Border.PaddingProperty, new Thickness(0));
            }
            OverrideAppearance(VersionLabel, TextBlock.FontFamilyProperty, font);
            OverrideAppearance(VersionLabel, TextBlock.FontSizeProperty, 10d);
            OverrideAppearance(VersionLabel, TextBlock.FontWeightProperty, FontWeights.Normal);
        }
        _productPlayer.SetEnabled(!refined);
        UpdateAppearanceClip();
    }

    private void UpdateAppearanceClip()
    {
        if (AppearanceContent.ActualWidth <= 0 || AppearanceContent.ActualHeight <= 0) return;
        // Border.ClipToBounds alone is rectangular. Clip the inner content to the
        // same inset round shape; works with this existing transparent WPF window.
        double radius = Math.Max(0, AppearanceFrame.CornerRadius.TopLeft - AppearanceFrame.BorderThickness.Left);
        var clip = new RectangleGeometry(new Rect(0,0,AppearanceContent.ActualWidth,AppearanceContent.ActualHeight),radius,radius);
        clip.Freeze();
        AppearanceContent.Clip = clip;
    }
}
