using System.Diagnostics;
using System.Windows;

namespace Emulate.Views;

public partial class LicenseWindow : Window
{
    public LicenseWindow() => InitializeComponent();

    private void Terms_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://developer.android.com/studio/terms") { UseShellExecute = true });

    private void AcceptBox_Click(object sender, RoutedEventArgs e) => ContinueButton.IsEnabled = AcceptBox.IsChecked == true;

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
