using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NetShaper.Views;

public partial class ToastNotification : Window
{
    public ToastNotification(Window owner, string title, string message, string hexColor = "#0A84FF")
    {
        InitializeComponent();
        Owner = owner;

        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        IndicatorBar.Background = brush;
        TitleText.Foreground = brush;
        TitleText.Text = title.ToUpperInvariant();
        MessageText.Text = message;

        Loaded += (_, _) =>
        {
            // Position in bottom-right corner of owner window
            if (Owner != null && Owner.WindowState != WindowState.Minimized)
            {
                Left = Owner.Left + Owner.Width - ActualWidth - 25;
                Top = Owner.Top + Owner.Height - ActualHeight - 40;
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Right - ActualWidth - 25;
                Top = workArea.Bottom - ActualHeight - 40;
            }

            // Slide & Fade in animation
            Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            BeginAnimation(OpacityProperty, fadeIn);

            // Auto dismiss timer
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                fadeOut.Completed += (_, _) => Close();
                BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        };
    }

    public static void Show(Window owner, string title, string message, string hexColor = "#0A84FF")
    {
        owner.Dispatcher.Invoke(() =>
        {
            var toast = new ToastNotification(owner, title, message, hexColor);
            toast.Show();
        });
    }
}
