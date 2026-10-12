using System.Diagnostics;
using System.Windows;
using Emulate.Models;
using Emulate.Services;

namespace Emulate.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private UpdateInfo? _available;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        CurrentVersionText.Text = $"Installed: v{UpdateService.CurrentVersion}";
        AutoCheck.IsChecked = AppSettings.Current.CheckForUpdatesOnStartup;
        RefreshPaths();
        FilesHelp.Text =
            "• In any app on the device, choose 'PC Bridge: pick from PC' (or 'Windows PC' in the file browser) to open File Explorer on this PC and send the picked file straight into the app.\n" +
            "• Anything in the shared folder appears in every device's gallery (Pictures > Emulate Shared).\n" +
            "• Share any file on the device to 'Save to PC' to copy it into Downloads\\Emulate.\n" +
            "• You can also drag files from Explorer onto the device window.";
    }

    private void RefreshPaths() =>
        PathsText.Text = $"SDK: {AppPaths.SdkRoot}\nDevices: {AppPaths.AvdRoot}\nShared folder: {AppPaths.SharedFolder}\nApp: {AppPaths.InstallDir}";

    // ---------- Updates ----------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking GitHub for a newer version...";
        try
        {
            _available = await UpdateService.CheckAsync();
            if (_available == null)
            {
                UpdateStatus.Text = $"You're up to date (v{UpdateService.CurrentVersion}).";
            }
            else
            {
                AppSettings.Current.SkippedVersion = null;
                AppSettings.Current.Save();
                UpdateStatus.Text = $"Version {_available.Version} is available." +
                                    (string.IsNullOrWhiteSpace(_available.Notes) ? "" : "\n" + Truncate(_available.Notes, 600));
                UpdateButtonText.Text = $"Update to v{_available.Version} & restart";
                UpdateButton.Visibility = Visibility.Visible;
                _main.ShowUpdateBanner(_available);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Could not check for updates: " + ex.Message;
            Log.Error("Update check failed", ex);
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_available == null) return;
        CheckButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        var progress = new Progress<DownloadProgress>(p =>
        {
            UpdateStatus.Text = p.ToString();
            UpdateProgress.IsIndeterminate = p.Total <= 0;
            UpdateProgress.Value = p.Fraction;
        });
        try
        {
            await UpdateService.DownloadAndRestartAsync(_available, progress);
            UpdateStatus.Text = "Restarting...";
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateStatus.Text = "Update failed: " + ex.Message;
            CheckButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Used by the main window's update banner.</summary>
    public static async Task RunUpdateAsync(Window owner, UpdateInfo info)
    {
        var progress = new Progress<DownloadProgress>(p => Log.Say(p.ToString()));
        try
        {
            await UpdateService.DownloadAndRestartAsync(info, progress);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            MessageBox.Show(owner, "Update failed: " + ex.Message, "Emulate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => UpdateService.Restart();

    private void Releases_Click(object sender, RoutedEventArgs e) => OpenUrl(_available?.PageUrl ?? UpdateService.ReleasesPage);

    private void AutoCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CheckForUpdatesOnStartup = AutoCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ---------- Emulator ----------

    private async void Accel_Click(object sender, RoutedEventArgs e)
    {
        AccelStatus.Text = "Checking...";
        if (!SdkInstaller.BaseToolsInstalled)
        {
            AccelStatus.Text = "Downloading the Android Emulator first...";
            try
            {
                await SdkInstaller.EnsureBaseToolsAsync(new Progress<DownloadProgress>(p => AccelStatus.Text = p.ToString()));
            }
            catch (Exception ex)
            {
                AccelStatus.Text = ex.Message;
                return;
            }
        }
        var (ok, details) = await SdkInstaller.CheckAccelerationAsync();
        AccelStatus.Text = (ok ? "✔ Virtualization is working. " : "✖ Virtualization is NOT available. Click 'Fix virtualization', then restart Windows. ") +
                           "\n" + details.Trim();
    }

    private void FixAccel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "This turns on the Windows 'Hypervisor Platform' and 'Virtual Machine Platform' features (needs administrator rights). " +
                "You will need to restart Windows afterwards.\n\nIf it still doesn't work, enable virtualization (Intel VT-x / AMD SVM) in your PC's BIOS.",
                "Fix virtualization", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            var cmd = "Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart; " +
                      "Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -All -NoRestart; " +
                      "Write-Host ''; Write-Host 'Done. Please restart Windows.' -ForegroundColor Green; Read-Host 'Press Enter to close'";
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Exception ex)
        {
            AccelStatus.Text = "Cancelled: " + ex.Message;
        }
    }

    private void Redownload_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceManager.Devices.Count > 0)
        {
            MessageBox.Show(this, "Stop all running devices first.", "Emulate");
            return;
        }
        try
        {
            SdkInstaller.ResetBaseTools();
            AccelStatus.Text = "Removed. The emulator will be downloaded again the next time you launch a device.";
        }
        catch (Exception ex)
        {
            AccelStatus.Text = ex.Message;
        }
    }

    private void Data_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(AppPaths.DataRoot);

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        MainWindow.OpenFolder(AppPaths.LogDir);
    }

    // ---------- Files ----------

    private void Shared_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.SharedFolder);
        MainWindow.OpenFolder(AppPaths.SharedFolder);
    }

    private void ChangeShared_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder to share with your devices", InitialDirectory = AppPaths.SharedFolder };
        if (dlg.ShowDialog(this) != true) return;
        AppSettings.Current.SharedFolderOverride = dlg.FolderName;
        AppSettings.Current.Save();
        RefreshPaths();
        Log.Say("Shared folder changed. Restart Emulate to start watching the new folder.");
    }

    private void Received_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.FromDeviceFolder);
        MainWindow.OpenFolder(AppPaths.FromDeviceFolder);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
