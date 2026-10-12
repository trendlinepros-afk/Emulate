using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using Emulate.Models;
using Emulate.Services;
using QRCoder;

namespace Emulate.Views;

public partial class WebAccessWindow : Window
{
    private readonly Profile _profile;

    public WebAccessWindow(Profile profile)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.ApplyDarkTitleBar(this);
        _profile = profile;
        HeaderText.Text = $"Web access · {profile.Name}";
        EnabledBox.IsChecked = profile.WebAccessEnabled;
        PortBox.Text = profile.WebPort > 0 ? profile.WebPort.ToString() : "auto";
        Refresh();
    }

    private RunningDevice? Device => DeviceManager.Find(_profile) is { IsRunning: true } d ? d : null;

    private void Refresh()
    {
        var on = _profile.WebAccessEnabled;
        var live = WebAccess.IsRunning(_profile);
        StateText.Text = !on ? "Off. Nobody can reach this device from a browser."
            : live ? "On. Open the link below on another device."
            : "On. It starts automatically whenever this device is running.";
        LivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (!live) return;

        var urls = WebAccess.Urls(_profile);
        var main = urls.FirstOrDefault() ?? "";
        UrlBox.Text = main;
        CodeText.Text = _profile.WebCode;
        OtherUrls.Text = urls.Count > 1 ? "Other addresses for this PC:\n" + string.Join("\n", urls.Skip(1)) : "";
        QrImage.Source = MakeQr(main);
    }

    private static BitmapImage MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private async Task ApplyAsync()
    {
        if (int.TryParse(PortBox.Text.Trim(), out var port) && port is > 1024 and < 65536 && port != _profile.WebPort)
        {
            _profile.WebPort = port;
            ProfileStore.Save();
            if (WebAccess.IsRunning(_profile)) await WebAccess.StopAsync(_profile); // restart on the new port below
        }
        if (_profile.WebAccessEnabled && Device is { } d && !WebAccess.IsRunning(_profile))
        {
            try { await WebAccess.StartAsync(d); }
            catch (Exception ex)
            {
                Log.Error("Web access start", ex);
                MessageBox.Show(this, "Couldn't start web access: " + ex.Message, "Emulate", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else if (!_profile.WebAccessEnabled && WebAccess.IsRunning(_profile))
        {
            await WebAccess.StopAsync(_profile);
        }
        PortBox.Text = _profile.WebPort > 0 ? _profile.WebPort.ToString() : "auto";
        Refresh();
    }

    private async void Enabled_Click(object sender, RoutedEventArgs e)
    {
        _profile.WebAccessEnabled = EnabledBox.IsChecked == true;
        WebAccess.EnsureCode(_profile);
        ProfileStore.Save();
        await ApplyAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(UrlBox.Text);
        Log.Say("Link copied.");
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"http://localhost:{_profile.WebPort}/?code={_profile.WebCode}") { UseShellExecute = true });

    private void NewCode_Click(object sender, RoutedEventArgs e)
    {
        _profile.WebCode = WebAccess.NewCode();
        ProfileStore.Save();
        Refresh();
        Log.Say("New access code set. Browsers using the old code will need the new link.");
    }

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        try { WebAccess.AllowThroughFirewall(); }
        catch (Exception ex) { Log.Say("Firewall rule not added: " + ex.Message); }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
        Close();
    }
}
