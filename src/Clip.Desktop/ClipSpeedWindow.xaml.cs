using System.Globalization;
using System.Windows;
using Clip.Core;

namespace Clip.Desktop;

public partial class ClipSpeedWindow : Window
{
    public double Speed { get; private set; }

    public ClipSpeedWindow(double speed)
    {
        InitializeComponent();
        WindowPresentation.FitInitialBounds(this);
        WindowPresentation.HideCaptionIcon(this);
        SpeedInput.Text = speed.ToString("0.###", CultureInfo.CurrentCulture);
        Loaded += (_, _) => { SpeedInput.Focus(); SpeedInput.SelectAll(); };
        SpeedInput.TextChanged += (_, _) => ValidationText.Visibility = Visibility.Collapsed;
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        var text = SpeedInput.Text.Trim().TrimEnd('×', 'x', 'X');
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var speed) &&
             !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out speed)) ||
            !double.IsFinite(speed) || speed < VideoClip.MinimumSpeed || speed > VideoClip.MaximumSpeed)
        {
            ValidationText.Visibility = Visibility.Visible;
            SpeedInput.Focus();
            return;
        }
        Speed = speed;
        DialogResult = true;
    }
}
