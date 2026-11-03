using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BoostStudio.App.Controls;

/// <summary>Fades and lifts new page content into place for a subtle navigation transition.</summary>
public sealed class AnimatedContentControl : ContentControl
{
    private readonly TranslateTransform _translate = new();

    public AnimatedContentControl()
    {
        RenderTransform = _translate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Focusable = false;
        IsTabStop = false;
    }

    public static readonly DependencyProperty AnimationMillisecondsProperty = DependencyProperty.Register(
        nameof(AnimationMilliseconds), typeof(int), typeof(AnimatedContentControl),
        new PropertyMetadata(230));

    public int AnimationMilliseconds
    {
        get => (int)GetValue(AnimationMillisecondsProperty);
        set => SetValue(AnimationMillisecondsProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is null || !IsLoaded) return;
        AnimateIn();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (Content is not null)
            Opacity = 1;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!IsLoaded || Content is null) return;
        if (Math.Abs(Opacity) < 0.001)
            AnimateIn();
    }

    private void AnimateIn()
    {
        Opacity = 0;
        _translate.Y = 10;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(AnimationMilliseconds, 80, 600));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        var move = new DoubleAnimation(10, 0, duration) { EasingFunction = easing };
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        _translate.BeginAnimation(TranslateTransform.YProperty, move, HandoffBehavior.SnapshotAndReplace);
    }
}
