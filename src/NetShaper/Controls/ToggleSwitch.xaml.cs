using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NetShaper.Controls;

public partial class ToggleSwitch : UserControl
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCheckedChanged));

    public static readonly RoutedEvent CheckedChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(CheckedChanged), RoutingStrategy.Bubble,
            typeof(RoutedPropertyChangedEventHandler<bool>), typeof(ToggleSwitch));

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public event RoutedPropertyChangedEventHandler<bool> CheckedChanged
    {
        add => AddHandler(CheckedChangedEvent, value);
        remove => RemoveHandler(CheckedChangedEvent, value);
    }

    public ToggleSwitch()
    {
        InitializeComponent();
        UpdateVisualState(false);
    }

    private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleSwitch ts)
        {
            ts.UpdateVisualState(true);
            ts.RaiseEvent(new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue, CheckedChangedEvent));
        }
    }

    private void OnToggleClicked(object sender, MouseButtonEventArgs e)
    {
        IsChecked = !IsChecked;
    }

    private void UpdateVisualState(bool animate)
    {
        double targetLeft = IsChecked ? (ActualWidth > 0 ? ActualWidth - 22 : 32) : 4;
        var activeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FF88"));
        var inactiveBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1B22"));
        var targetBrush = IsChecked ? activeBrush : inactiveBrush;

        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Knob.BeginAnimation(Canvas.LeftProperty, anim);

            var colorAnim = new ColorAnimation
            {
                To = targetBrush.Color,
                Duration = TimeSpan.FromMilliseconds(160)
            };
            TrackBorder.Background = new SolidColorBrush(((SolidColorBrush)TrackBorder.Background).Color);
            TrackBorder.Background.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
        else
        {
            Canvas.SetLeft(Knob, targetLeft);
            TrackBorder.Background = targetBrush;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateVisualState(false);
    }
}
