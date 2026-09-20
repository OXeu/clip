using System.Windows;

namespace Clip.Desktop;

public partial class ClipNameWindow : Window
{
    public string ClipName => NameInput.Text.Trim();

    public ClipNameWindow(string name)
    {
        InitializeComponent();
        WindowPresentation.FitInitialBounds(this);
        WindowPresentation.HideCaptionIcon(this);
        NameInput.Text = name;
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
        NameInput.TextChanged += (_, _) => ValidationText.Visibility = Visibility.Collapsed;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ClipName))
        {
            ValidationText.Visibility = Visibility.Visible;
            NameInput.Focus();
            return;
        }
        DialogResult = true;
    }
}
