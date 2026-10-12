using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Emulate.Models;
using Emulate.Services;

namespace Emulate.Views;

/// <summary>One running device: its screen (embedded or popped out) plus a toolbar.</summary>
public partial class DeviceView : UserControl
{
    public RunningDevice Device { get; }

    public DeviceView(RunningDevice device)
    {
        InitializeComponent();
        Device = device;
        DataContext = device;
        device.PropertyChanged += OnDeviceChanged;
        Loaded += (_, _) => Refresh();
        _retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _retry.Tick += (_, _) => { _retry.Stop(); Refresh(); };
    }

    private readonly System.Windows.Threading.DispatcherTimer _retry;

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunningDevice.EmulatorWindow) or nameof(RunningDevice.Docked) or nameof(RunningDevice.State))
            Refresh();
    }

    /// <summary>Shows the right panel and attaches/detaches the emulator window.</summary>
    private void Refresh()
    {
        var d = Device;
        var failed = d.State == DeviceState.Failed;
        var hasWindow = d.EmulatorWindow != IntPtr.Zero;

        if (d.Docked && hasWindow && !failed)
        {
            if (!Host.IsAttached) Host.Attach(d.EmulatorWindow);
            // The host's native container may not exist yet (first layout pass): try again shortly.
            if (!Host.IsAttached) _retry.Start();
        }
        else if (Host.IsAttached)
        {
            Host.Detach();
        }

        // The native window always draws on top of WPF, so only one of these may be visible at a time.
        var showScreen = d.Docked && Host.IsAttached && !failed;
        Host.Visibility = showScreen ? Visibility.Visible : Visibility.Collapsed;
        FailedPanel.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        UndockedPanel.Visibility = !failed && !d.Docked && hasWindow ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = !failed && !showScreen && UndockedPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ProgressTitle.Text = d.State switch
        {
            DeviceState.Preparing => "Getting things ready",
            DeviceState.Starting => "Starting",
            DeviceState.Booting => "Booting Android",
            DeviceState.Configuring => "Almost there",
            DeviceState.Stopping => "Shutting down",
            DeviceState.Running => "Connecting the screen",
            _ => "Starting",
        };

        DockIcon.Text = d.Docked ? "" : ""; // pop out / back in
        DockButton.ToolTip = d.Docked ? "Open in its own window" : "Show inside Emulate";
    }

    /// <summary>Called before the app closes or updates so the emulator window isn't destroyed with ours.</summary>
    public void ReleaseWindow() => Host.Detach();

    // ---------- Toolbar ----------

    private void Back_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyBack);
    private void Home_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyHome);
    private void Recents_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyRecents);
    private void Power_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyPower);
    private void VolUp_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyVolumeUp);
    private void VolDown_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.SendKeyAsync(Device, DeviceManager.KeyVolumeDown);
    private void Rotate_Click(object sender, RoutedEventArgs e) => _ = DeviceManager.RotateAsync(Device);

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        Device.Docked = !Device.Docked;
        Device.Profile.LaunchDocked = Device.Docked;
        ProfileStore.Save();
        if (!Device.Docked) DeviceManager.BringToFront(Device);
    }

    private void Front_Click(object sender, RoutedEventArgs e) => DeviceManager.BringToFront(Device);

    private async void Stop_Click(object sender, RoutedEventArgs e) => await DeviceManager.StopAsync(Device);

    private async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Send files to {Device.Name}",
            Multiselect = true,
            Filter = "All files (*.*)|*.*|Photos & videos|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.heic;*.mp4;*.mov;*.mkv;*.webm",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Log.Say($"Sending {dlg.FileNames.Length} file(s) to {Device.Name}...");
        var n = await DeviceManager.SendFilesAsync(Device, dlg.FileNames);
        Log.Say($"Sent {n} file(s). Photos and videos are in the gallery (Pictures › From PC), other files in Downloads.");
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        Log.Say($"Copying photos and downloads from {Device.Name}...");
        var folder = await DeviceManager.PullMediaAsync(Device);
        Log.Say($"Copied to {folder}");
        MainWindow.OpenFolder(folder);
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await DeviceManager.ScreenshotAsync(Device);
            Log.Say("Screenshot saved: " + file);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Say("Screenshot failed: " + ex.Message);
        }
    }

    private async void Apk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Install an Android app (.apk)", Filter = "Android apps (*.apk)|*.apk" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Log.Say("Installing " + Path.GetFileName(dlg.FileName) + "...");
        Log.Say(await DeviceManager.InstallApkAsync(Device, dlg.FileName));
    }

    private async void Location_Click(object sender, RoutedEventArgs e)
    {
        // Dialogs can't sit above the native emulator window, so hide it while the dialog is open.
        Host.Visibility = Visibility.Collapsed;
        try
        {
            var dlg = new ProfileWindow(Device.Profile, Device.Profile.Os, locationOnly: true) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ProfileStore.Save();
                await DeviceManager.PushLocationNowAsync(Device);
            }
        }
        finally
        {
            Refresh();
        }
    }
}
