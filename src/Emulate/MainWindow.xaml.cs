using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Emulate.Models;
using Emulate.Services;
using Emulate.Views;

namespace Emulate;

public sealed class OsItem
{
    public OsItem(OsDefinition os) => Os = os;
    public OsDefinition Os { get; }
    public string Subtitle => $"{Os.Version} · {Os.Category}";
    public string CountText
    {
        get
        {
            var n = ProfileStore.ForOs(Os.Id).Count();
            return n == 0 ? "" : n.ToString();
        }
    }
}

public sealed class ProfileItem
{
    public ProfileItem(Profile p) => Profile = p;
    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string Initial => string.IsNullOrEmpty(Profile.Name) ? "?" : Profile.Name[..1].ToUpperInvariant();
    public string Summary => $"{Profile.Device.Name} · {Profile.RamMb / 1024.0:0.#} GB RAM · {Profile.StorageGb} GB storage";
    public string LastUsedText => Profile.LastUsed is { } d ? $"Last used {d:g}" : $"Created {Profile.Created:g}";
    private RunningDevice? Device => DeviceManager.Find(Profile);
    public bool IsRunning => Device != null;
    public string RunningText => Device?.IsRunning == true ? "Running" : "Starting";
    public string LaunchText => IsRunning ? "Open" : "Launch";
    public bool ShowLaunchChevron => !IsRunning;
}

public partial class MainWindow : Window
{
    private UpdateInfo? _pendingUpdate;
    private readonly Dictionary<RunningDevice, DeviceView> _views = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.ApplyDarkTitleBar(this);
        VersionText.Text = "v" + UpdateService.CurrentVersion;
        Log.Status += msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg);

        DeviceTabs.ItemsSource = DeviceManager.Devices;
        DeviceManager.Devices.CollectionChanged += OnDevicesChanged;

        RefreshOsList();
        OsList.SelectedIndex = 0;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.JustUpdatedTo != null) Log.Say($"Updated to version {App.JustUpdatedTo}.");
        if (App.UpdateFailed) Log.Say("The last update could not be applied. See Settings › Open logs (update.log).");

        await DeviceManager.ReattachAsync();

        if (AppSettings.Current.CheckForUpdatesOnStartup)
        {
            try
            {
                var info = await UpdateService.CheckAsync();
                if (info != null) ShowUpdateBanner(info);
            }
            catch (Exception ex)
            {
                Log.Info("Startup update check: " + ex.Message);
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) => ReleaseEmbeddedWindows();

    /// <summary>Hands every embedded emulator window back to the desktop so it survives our window closing.</summary>
    public void ReleaseEmbeddedWindows()
    {
        foreach (var v in _views.Values) v.ReleaseWindow();
    }

    public void ShowUpdateBanner(UpdateInfo info)
    {
        _pendingUpdate = info;
        UpdatePillText.Text = $"Update to v{info.Version}";
        UpdatePill.Visibility = Visibility.Visible;
    }

    // ---------- Tabs / device views ----------

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (RunningDevice d in e.NewItems)
            {
                var view = new DeviceView(d) { Visibility = Visibility.Collapsed };
                _views[d] = view;
                DeviceViews.Children.Add(view);
                d.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(RunningDevice.State)) RefreshProfiles(); };
                // Jump to the new device straight away.
                Dispatcher.BeginInvoke(() => SelectDevice(d), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        if (e.OldItems != null)
        {
            foreach (RunningDevice d in e.OldItems)
            {
                if (!_views.Remove(d, out var view)) continue;
                view.ReleaseWindow();
                DeviceViews.Children.Remove(view);
                if (view.Visibility == Visibility.Visible) HomeTab.IsChecked = true;
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var v in _views.Values) { v.ReleaseWindow(); DeviceViews.Children.Remove(v); }
            _views.Clear();
            HomeTab.IsChecked = true;
        }
        RefreshOsList();
        RefreshProfiles();
    }

    private void SelectDevice(RunningDevice d)
    {
        var container = DeviceTabs.ItemContainerGenerator.ContainerFromItem(d) as ContentPresenter;
        var radio = container != null ? FindChild<RadioButton>(container) : null;
        if (radio != null) radio.IsChecked = true;
        else ShowView(d);
    }

    private void ShowView(RunningDevice? d)
    {
        HomePanel.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (dev, view) in _views) view.Visibility = dev == d ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HomeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DeviceViews != null) ShowView(null);
    }

    private void DeviceTab_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RunningDevice d) ShowView(d);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // ---------- OS + profiles ----------

    private OsDefinition SelectedOs => (OsList.SelectedItem as OsItem)?.Os ?? OsCatalog.All[0];

    private void RefreshOsList()
    {
        var selected = Math.Max(0, OsList.SelectedIndex);
        OsList.ItemsSource = OsCatalog.All.Select(o => new OsItem(o)).ToList();
        OsList.SelectedIndex = selected;
    }

    private void RefreshProfiles()
    {
        var os = SelectedOs;
        OsTitle.Text = os.Name;
        OsTagline.Text = os.Tagline;
        OsDescription.Text = os.Description;
        OsChips.ItemsSource = new[]
        {
            "Google Play",
            $"{os.Version} · {os.ApiLabel}",
            os.Category,
            SdkInstaller.IsImageInstalled(os) ? "Downloaded" : "≈1.5 GB download on first launch",
        };
        var items = ProfileStore.ForOs(os.Id).Select(p => new ProfileItem(p)).ToList();
        ProfileList.ItemsSource = items;
        NoProfiles.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshProfiles();

    private static Profile? ProfileOf(object sender) => ((sender as FrameworkElement)?.DataContext as ProfileItem)?.Profile;

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileWindow(null, SelectedOs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ProfileStore.Add(dlg.Profile);
        RefreshOsList();
        RefreshProfiles();
        Log.Say($"Profile '{dlg.Profile.Name}' created. Press Launch to start it.");
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) EditProfile(p);
    }

    private void EditProfile(Profile p)
    {
        var dlg = new ProfileWindow(p, p.Os) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ProfileStore.Save();
        RefreshProfiles();
        if (DeviceManager.Find(p) is { IsRunning: true })
            Log.Say("Saved. Screen, memory and camera changes apply the next time this device starts; location applies now.");
    }

    /// <summary>Launch asks where to show the device (or jumps to it if it's already running).</summary>
    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        if (DeviceManager.Find(p) is { } running)
        {
            SelectDevice(running);
            return;
        }
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Launch in window", "", "Show the device inside Emulate", () => _ = LaunchAsync(p, docked: true)));
        menu.Items.Add(MenuItemFor("Launch in new window", "", "Open the device in its own window", () => _ = LaunchAsync(p, docked: false)));
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 6;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, string glyph, string? tip, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"], FontSize = 14 },
            ToolTip = tip,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void ProfileCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ProfileOf(sender) is not { } p) return;
        if (DeviceManager.Find(p) is { } running) SelectDevice(running);
        else _ = LaunchAsync(p, p.LaunchDocked);
    }

    private void ProfileMore_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Launch in window", "", null, () => _ = LaunchAsync(p, true)));
        menu.Items.Add(MenuItemFor("Launch in new window", "", null, () => _ = LaunchAsync(p, false)));
        menu.Items.Add(MenuItemFor("Edit", "", null, () => EditProfile(p)));
        menu.Items.Add(MenuItemFor("Duplicate settings", "", "New profile with the same settings (apps and data are not copied)", () =>
        {
            ProfileStore.Add(new Profile
            {
                Name = p.Name + " (copy)", OsId = p.OsId, DeviceTypeId = p.DeviceTypeId, RamMb = p.RamMb,
                StorageGb = p.StorageGb, CpuCores = p.CpuCores, Camera = p.Camera, Location = p.Location,
                ManualLatitude = p.ManualLatitude, ManualLongitude = p.ManualLongitude, SyncSharedFolder = p.SyncSharedFolder,
                LaunchDocked = p.LaunchDocked,
            });
            RefreshOsList();
            RefreshProfiles();
        }));
        menu.Items.Add(MenuItemFor("Copy profile (apps & data)…", "\uE8C8", "An exact copy: same apps, accounts, photos and settings", () => _ = CopyProfileAsync(p)));
        menu.Items.Add(MenuItemFor("Web access…", "\uE774", "Use this device from a browser on another device", () =>
            new WebAccessWindow(p) { Owner = this }.ShowDialog()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Factory reset…", "", "Erase apps and data", () =>
        {
            if (DeviceManager.Find(p) != null) { MessageBox.Show(this, "Stop the device first.", "Emulate"); return; }
            if (MessageBox.Show(this, $"Erase everything on '{p.Name}'? Apps, accounts and files on the device will be deleted.",
                    "Factory reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            AvdWriter.Wipe(p);
            Log.Say($"'{p.Name}' was reset. It will boot like a new device.");
        }));
        menu.Items.Add(MenuItemFor("Delete profile…", "", null, () =>
        {
            if (DeviceManager.Find(p) != null) { MessageBox.Show(this, "Stop the device first.", "Emulate"); return; }
            if (MessageBox.Show(this, $"Delete '{p.Name}' and everything on it? This cannot be undone.",
                    "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            AvdWriter.Delete(p);
            ProfileStore.Remove(p);
            RefreshOsList();
            RefreshProfiles();
        }));
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task CopyProfileAsync(Profile p)
    {
        if (DeviceManager.Find(p) != null)
        {
            MessageBox.Show(this, $"Stop '{p.Name}' first, then copy it. (Copying a running device could give a broken copy.)", "Copy profile");
            return;
        }
        var name = PromptWindow.Ask(this, "Copy profile", $"An exact copy of '{p.Name}', with all its apps, accounts and files. Name for the copy:", p.Name + " copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        var progress = new Progress<DownloadProgress>(d => Log.Say(d.ToString()));
        try
        {
            var copy = await ProfileCloner.CloneAsync(p, name.Trim(), progress);
            RefreshOsList();
            RefreshProfiles();
            Log.Say($"'{copy.Name}' is ready: an exact copy of '{p.Name}'.");
        }
        catch (Exception ex)
        {
            Log.Error("Copy profile", ex);
            MessageBox.Show(this, "Couldn't copy the profile: " + ex.Message, "Copy profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LaunchAsync(Profile p, bool docked)
    {
        if (ProfileCloner.IsBusy(p))
        {
            MessageBox.Show(this, "This profile is being copied. Try again when the copy is finished.", "Emulate");
            return;
        }
        if (!AppSettings.Current.AcceptedSdkLicense)
        {
            var license = new LicenseWindow { Owner = this };
            if (license.ShowDialog() != true) return;
            AppSettings.Current.AcceptedSdkLicense = true;
            AppSettings.Current.Save();
        }
        Log.Say($"Starting {p.Name}…");
        await DeviceManager.LaunchAsync(p, docked);
        RefreshOsList();
        RefreshProfiles();
    }

    // ---------- Top bar / status bar ----------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(this) { Owner = this }.ShowDialog();
        RefreshProfiles();
    }

    private async void UpdatePill_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        if (MessageBox.Show(this, $"Update Emulate to version {_pendingUpdate.Version} now? The app will restart. Running devices keep running.",
                "Update", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        UpdatePill.IsEnabled = false;
        await SettingsWindow.RunUpdateAsync(this, _pendingUpdate);
        UpdatePill.IsEnabled = true;
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
