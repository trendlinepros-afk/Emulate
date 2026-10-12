using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Emulate.Models;
using Emulate.Services;
using Emulate.Views;

namespace Emulate;

public sealed class OsItem
{
    public OsItem(OsDefinition os) => Os = os;
    public OsDefinition Os { get; }

    public string Footer
    {
        get
        {
            var count = ProfileStore.ForOs(Os.Id).Count();
            var dl = SdkInstaller.IsImageInstalled(Os) ? "Downloaded" : "About 1.5 GB download on first launch";
            return $"Google Play  ·  {count} profile{(count == 1 ? "" : "s")}  ·  {dl}";
        }
    }
}

public sealed class ProfileItem
{
    public ProfileItem(Profile p) => Profile = p;
    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string Summary => $"{Profile.Device.Name}  ·  {Profile.RamMb / 1024.0:0.#} GB RAM  ·  {Profile.StorageGb} GB storage";
    public string LastUsedText => Profile.LastUsed is { } d ? $"Last used {d:g}" : $"Created {Profile.Created:g}";
}

public partial class MainWindow : Window
{
    private UpdateInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + UpdateService.CurrentVersion;
        Log.Status += msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg);

        DeviceList.ItemsSource = DeviceManager.Devices;
        DeviceManager.Devices.CollectionChanged += (_, _) => UpdateDevicesEmpty();
        UpdateDevicesEmpty();

        RefreshOsList();
        OsList.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.JustUpdatedTo != null) Log.Say($"Updated to version {App.JustUpdatedTo}.");
        if (App.UpdateFailed) Log.Say("The last update could not be applied. See Settings > Open logs (update.log).");

        await DeviceManager.ReattachAsync();

        if (AppSettings.Current.CheckForUpdatesOnStartup)
        {
            try
            {
                var info = await UpdateService.CheckAsync();
                if (info != null && info.Tag != AppSettings.Current.SkippedVersion) ShowUpdateBanner(info);
            }
            catch (Exception ex)
            {
                Log.Info("Startup update check: " + ex.Message);
            }
        }
    }

    public void ShowUpdateBanner(UpdateInfo info)
    {
        _pendingUpdate = info;
        UpdateBannerText.Text = $"Version {info.Version} is available (you have {UpdateService.CurrentVersion}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateDevicesEmpty() =>
        NoDevices.Visibility = DeviceManager.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- OS + profiles ----------

    private OsDefinition SelectedOs => (OsList.SelectedItem as OsItem)?.Os ?? OsCatalog.All[0];

    private void RefreshOsList()
    {
        var selected = OsList.SelectedIndex;
        OsList.ItemsSource = OsCatalog.All.Select(o => new OsItem(o)).ToList();
        OsList.SelectedIndex = selected;
    }

    private void RefreshProfiles()
    {
        var os = SelectedOs;
        ProfilesHeader.Text = $"2   Choose a profile for {os.Name}";
        var items = ProfileStore.ForOs(os.Id).Select(p => new ProfileItem(p)).ToList();
        ProfileList.ItemsSource = items;
        NoProfiles.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshProfiles();

    private static Profile? ProfileOf(object sender) => ((sender as FrameworkElement)?.DataContext as ProfileItem)?.Profile;
    private static RunningDevice? DeviceOf(object sender) => (sender as FrameworkElement)?.DataContext as RunningDevice;

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileWindow(null, SelectedOs) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ProfileStore.Add(dlg.Profile);
            RefreshOsList();
            RefreshProfiles();
            ProfileList.SelectedItem = (ProfileList.ItemsSource as List<ProfileItem>)?.FirstOrDefault(i => i.Profile == dlg.Profile);
            if (MessageBox.Show(this, $"Profile '{dlg.Profile.Name}' created. Launch it now?", "Emulate",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = LaunchAsync(dlg.Profile);
        }
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) EditProfile(p);
    }

    private void EditProfile(Profile p)
    {
        var dlg = new ProfileWindow(p, p.Os) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ProfileStore.Save();
            RefreshProfiles();
            if (DeviceManager.Find(p) is { IsRunning: true })
                Log.Say("Saved. Screen, memory and camera changes apply the next time this device starts; location applies now.");
        }
    }

    private void ProfileMore_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Launch", () => _ = LaunchAsync(p));
        Add("Edit...", () => EditProfile(p));
        Add("Duplicate settings into a new profile", () =>
        {
            var copy = new Profile
            {
                Name = p.Name + " (copy)", OsId = p.OsId, DeviceTypeId = p.DeviceTypeId, RamMb = p.RamMb,
                StorageGb = p.StorageGb, CpuCores = p.CpuCores, Camera = p.Camera, Location = p.Location,
                ManualLatitude = p.ManualLatitude, ManualLongitude = p.ManualLongitude, SyncSharedFolder = p.SyncSharedFolder,
            };
            ProfileStore.Add(copy);
            RefreshOsList();
            RefreshProfiles();
        });
        menu.Items.Add(new Separator());
        Add("Factory reset (erase apps and data)...", () =>
        {
            if (DeviceManager.Find(p) != null) { MessageBox.Show(this, "Stop the device first.", "Emulate"); return; }
            if (MessageBox.Show(this, $"Erase everything on '{p.Name}'? Apps, accounts and files on the device will be deleted.",
                    "Factory reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            AvdWriter.Wipe(p);
            Log.Say($"'{p.Name}' was reset. It will boot like a new device.");
        });
        Add("Delete profile...", () =>
        {
            if (DeviceManager.Find(p) != null) { MessageBox.Show(this, "Stop the device first.", "Emulate"); return; }
            if (MessageBox.Show(this, $"Delete '{p.Name}' and everything on it? This cannot be undone.",
                    "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            AvdWriter.Delete(p);
            ProfileStore.Remove(p);
            RefreshOsList();
            RefreshProfiles();
        });
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) _ = LaunchAsync(p);
    }

    private void ProfileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileItem item) _ = LaunchAsync(item.Profile);
    }

    private async Task LaunchAsync(Profile p)
    {
        if (!AppSettings.Current.AcceptedSdkLicense)
        {
            var license = new LicenseWindow { Owner = this };
            if (license.ShowDialog() != true) return;
            AppSettings.Current.AcceptedSdkLicense = true;
            AppSettings.Current.Save();
        }
        Log.Say($"Starting {p.Name}...");
        await DeviceManager.LaunchAsync(p);
        RefreshOsList();
        RefreshProfiles();
    }

    // ---------- Running device actions ----------

    private async void DevSendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Send files to {d.Name}",
            Multiselect = true,
            Filter = "All files (*.*)|*.*|Photos & videos|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.heic;*.mp4;*.mov;*.mkv;*.webm",
        };
        if (dlg.ShowDialog(this) != true) return;
        Log.Say($"Sending {dlg.FileNames.Length} file(s) to {d.Name}...");
        var n = await DeviceManager.SendFilesAsync(d, dlg.FileNames);
        Log.Say($"Sent {n} file(s) to {d.Name}. Photos/videos are in the gallery (Pictures > From PC), other files in Downloads.");
    }

    private async void DevPull_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        Log.Say($"Copying photos and downloads from {d.Name}...");
        var folder = await DeviceManager.PullMediaAsync(d);
        Log.Say($"Copied to {folder}");
        OpenFolder(folder);
    }

    private async void DevScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        try
        {
            var file = await DeviceManager.ScreenshotAsync(d);
            Log.Say("Screenshot saved: " + file);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Say("Screenshot failed: " + ex.Message);
        }
    }

    private async void DevApk_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Install an Android app (.apk)", Filter = "Android apps (*.apk)|*.apk" };
        if (dlg.ShowDialog(this) != true) return;
        Log.Say("Installing " + Path.GetFileName(dlg.FileName) + "...");
        Log.Say(await DeviceManager.InstallApkAsync(d, dlg.FileName));
    }

    private async void DevLocation_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        var dlg = new ProfileWindow(d.Profile, d.Profile.Os, locationOnly: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ProfileStore.Save();
            await DeviceManager.PushLocationNowAsync(d);
        }
    }

    private void DevShow_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is { } d) DeviceManager.BringToFront(d);
    }

    private async void DevStop_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is { } d) await DeviceManager.StopAsync(d);
    }

    // ---------- Header / footer ----------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(this) { Owner = this }.ShowDialog();
        RefreshOsList();
    }

    private async void BannerUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        await SettingsWindow.RunUpdateAsync(this, _pendingUpdate);
    }

    private void BannerLater_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
        {
            AppSettings.Current.SkippedVersion = _pendingUpdate.Tag;
            AppSettings.Current.Save();
        }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OpenShared_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.SharedFolder);
        OpenFolder(AppPaths.SharedFolder);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        OpenFolder(AppPaths.LogDir);
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
}
