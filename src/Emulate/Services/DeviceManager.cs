using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using Emulate.Models;

namespace Emulate.Services;

public enum DeviceState { Preparing, Starting, Booting, Configuring, Running, Stopping, Stopped, Failed }

/// <summary>A device (profile) that is starting or running. Bound directly to the "Running devices" list.</summary>
public sealed class RunningDevice : INotifyPropertyChanged
{
    public RunningDevice(Profile profile) => Profile = profile;

    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string OsName => Profile.Os.Name;
    public string Accent => Profile.Os.Accent;
    public Process? Process { get; set; }
    public int Port { get; set; }
    public string Serial => $"emulator-{Port}";
    internal CancellationTokenSource Cts { get; } = new();

    private DeviceState _state = DeviceState.Preparing;
    public DeviceState State
    {
        get => _state;
        set { _state = value; OnChanged(); OnChanged(nameof(IsRunning)); OnChanged(nameof(IsBusy)); OnChanged(nameof(StateText)); }
    }

    private string _detail = "";
    public string Detail { get => _detail; set { _detail = value; OnChanged(); OnChanged(nameof(StateText)); } }

    private double _progress = -1;
    /// <summary>0..1 while downloading, -1 for indeterminate.</summary>
    public double Progress { get => _progress; set { _progress = value; OnChanged(); OnChanged(nameof(ShowProgress)); OnChanged(nameof(ProgressIndeterminate)); OnChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => Math.Max(0, _progress) * 100;
    public bool ProgressIndeterminate => _progress < 0;
    public bool ShowProgress => _state is DeviceState.Preparing or DeviceState.Starting or DeviceState.Booting or DeviceState.Configuring;

    private string _location = "";
    public string LocationText { get => _location; set { _location = value; OnChanged(); } }

    private bool _docked = true;
    /// <summary>True when the device screen is shown inside the Emulate window.</summary>
    public bool Docked { get => _docked; set { _docked = value; OnChanged(); } }

    private IntPtr _window;
    /// <summary>The emulator's native window, once it has appeared.</summary>
    public IntPtr EmulatorWindow { get => _window; set { _window = value; OnChanged(); } }

    public string Initial => string.IsNullOrEmpty(Profile.Name) ? "?" : Profile.Name[..1].ToUpperInvariant();

    public bool IsRunning => _state == DeviceState.Running;
    public bool IsBusy => ShowProgress;
    public string StateText => string.IsNullOrEmpty(_detail) ? _state.ToString() : $"{_state}: {_detail}";

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void OnChanged([CallerMemberName] string? name = null) =>
        Application.Current?.Dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}

/// <summary>Starts, configures, monitors and stops emulated devices.</summary>
public static class DeviceManager
{
    public static ObservableCollection<RunningDevice> Devices { get; } = new();

    static DeviceManager()
    {
        // A device that goes away takes its web access server with it.
        Devices.CollectionChanged += (_, e) =>
        {
            if (e.OldItems == null) return;
            foreach (RunningDevice d in e.OldItems)
                if (Find(d.Profile) == null) _ = WebAccess.StopAsync(d.Profile);
        };
    }
    private static FileSystemWatcher? _sharedWatcher;
    private static CancellationTokenSource? _sharedDebounce;

    public static RunningDevice? Find(Profile p) => Devices.FirstOrDefault(d => d.Profile.Id == p.Id);

    public static async Task LaunchAsync(Profile profile, bool docked)
    {
        var existing = Find(profile);
        if (existing != null && existing.State is not (DeviceState.Stopped or DeviceState.Failed))
        {
            if (existing.IsRunning) BringToFront(existing);
            return;
        }
        if (existing != null) Devices.Remove(existing);

        var dev = new RunningDevice(profile) { Docked = docked };
        if (profile.LaunchDocked != docked)
        {
            profile.LaunchDocked = docked;
            ProfileStore.Save();
        }
        Devices.Add(dev);
        try
        {
            await StartAsync(dev);
        }
        catch (OperationCanceledException)
        {
            if (dev.State != DeviceState.Failed)
            {
                dev.State = DeviceState.Stopped;
                dev.Detail = "";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Launch of {profile.Name} failed", ex);
            dev.State = DeviceState.Failed;
            dev.Detail = ex.Message;
            dev.Progress = 0;
            Log.Say($"{profile.Name} failed to start: {ex.Message}");
        }
    }

    private static async Task StartAsync(RunningDevice dev)
    {
        var p = dev.Profile;
        var os = p.Os;
        var ct = dev.Cts.Token;
        var progress = new Progress<DownloadProgress>(dp =>
        {
            dev.Detail = dp.ToString();
            dev.Progress = dp.Total > 0 ? dp.Fraction : -1;
        });

        dev.State = DeviceState.Preparing;
        dev.Detail = "Checking Android tools";
        await SdkInstaller.EnsureBaseToolsAsync(progress, ct);
        dev.Detail = $"Checking {os.Version} system image";
        await SdkInstaller.EnsureSystemImageAsync(os, progress, ct);
        dev.Progress = -1;

        AvdWriter.Write(p);
        p.LastUsed = DateTime.Now;
        ProfileStore.Save();

        dev.State = DeviceState.Starting;
        dev.Detail = "Launching emulator";
        await Adb.EnsureServerAsync();
        dev.Port = FindFreePort();

        var args = new List<string>
        {
            "-avd", p.AvdName,
            "-port", dev.Port.ToString(),
            "-gpu", "auto",
            "-netdelay", "none",
            "-netspeed", "full",
        };
        var psi = ProcessRunner.CreateStartInfo(AppPaths.Emulator, args);
        psi.WorkingDirectory = Path.GetDirectoryName(AppPaths.Emulator)!;
        var logFile = Path.Combine(AppPaths.LogDir, $"emulator-{p.AvdName}.log");
        Directory.CreateDirectory(AppPaths.LogDir);
        var logWriter = new StreamWriter(logFile, false) { AutoFlush = true };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var recent = new Queue<string>();
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (logWriter)
            {
                logWriter.WriteLine(line);
                recent.Enqueue(line);
                while (recent.Count > 15) recent.Dequeue();
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Exited += (_, _) =>
        {
            string tail;
            lock (logWriter) { tail = string.Join("\n", recent); logWriter.Dispose(); }
            var code = SafeExitCode(proc);
            Log.Info($"Emulator for {p.Name} exited with {code}");
            if (code == 0 && dev.State is DeviceState.Starting or DeviceState.Booting)
            {
                // The launcher handed off to the VM process; keep going and track the device through adb.
                dev.Process = FindEmulatorProcess(p.AvdName);
                return;
            }
            if (dev.State == DeviceState.Running && code == 0)
            {
                // Some emulator builds hand off to a child process; if adb still sees the device, keep tracking it.
                dev.Process = FindEmulatorProcess(p.AvdName);
                if (dev.Process != null) return;
            }
            if (dev.State is DeviceState.Stopping or DeviceState.Running or DeviceState.Stopped)
            {
                dev.State = DeviceState.Stopped;
                dev.Detail = "";
                dev.Cts.Cancel();
                Application.Current?.Dispatcher.BeginInvoke(() => Devices.Remove(dev));
            }
            else
            {
                // Keep failed devices in the list so the reason stays visible.
                dev.State = DeviceState.Failed;
                dev.Detail = ExplainEmulatorFailure(tail);
                dev.Cts.Cancel();
                Log.Say($"{p.Name}: {dev.Detail}");
            }
        };
        Log.Info($"Starting emulator: {AppPaths.Emulator} {string.Join(' ', args)}");
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        dev.Process = proc;
        _ = FindWindowAsync(dev);

        dev.State = DeviceState.Booting;
        dev.Detail = "Booting Android (first boot can take a few minutes)";
        await WaitForBootAsync(dev, ct);
        await AfterBootAsync(dev);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static string ExplainEmulatorFailure(string tail)
    {
        if (tail.Contains("x86_64 emulation currently requires hardware acceleration", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("WHPX", StringComparison.OrdinalIgnoreCase) || tail.Contains("acceleration", StringComparison.OrdinalIgnoreCase))
            return "Hardware acceleration is not available. Open Settings > Fix virtualization, then restart Windows.";
        var last = tail.Split('\n').LastOrDefault(l => l.Contains("ERROR") || l.Contains("FATAL") || l.Contains("PANIC"));
        return last?.Trim() ?? "The emulator closed unexpectedly. See the log in Settings > Open logs.";
    }

    private static int FindFreePort()
    {
        var used = Devices.Select(d => d.Port).ToHashSet();
        for (var port = 5554; port <= 5682; port += 2)
        {
            if (used.Contains(port)) continue;
            if (PortFree(port) && PortFree(port + 1)) return port;
        }
        throw new InvalidOperationException("Too many emulators are running.");
    }

    private static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForBootAsync(RunningDevice dev, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var r = await Adb.RunAsync(TimeSpan.FromSeconds(15), "-s", dev.Serial, "shell", "getprop sys.boot_completed");
            if (r.Ok && r.Output.Trim() == "1") return;
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("Android did not finish booting within 10 minutes.");
    }

    /// <summary>Everything that runs once a device has booted (also used when re-attaching after an update).</summary>
    private static async Task AfterBootAsync(RunningDevice dev)
    {
        var p = dev.Profile;
        dev.State = DeviceState.Configuring;
        dev.Detail = "Setting up";
        try
        {
            // Undo a browser "fit to screen" size that may be left over from before a restart.
            await Adb.ShellAsync(dev.Serial, "wm size reset");
            if (!p.PresetApplied) await Presets.ApplyAsync(dev.Serial, p);
            await InstallBridgeAsync(dev);
            await SendBridgeConfigAsync(dev);
        }
        catch (Exception ex)
        {
            Log.Error($"Post-boot setup for {p.Name}", ex);
        }

        dev.State = DeviceState.Running;
        dev.Detail = "";
        dev.Progress = 0;
        Log.Say($"{p.Name} is ready.");

        _ = LocationLoopAsync(dev);
        _ = LivenessLoopAsync(dev);
        if (p.WebAccessEnabled && !WebAccess.IsRunning(p))
        {
            try { await WebAccess.StartAsync(dev); }
            catch (Exception ex) { Log.Error("Web access start", ex); Log.Say($"Web access for {p.Name} couldn't start: {ex.Message}"); }
        }
        if (p.SyncSharedFolder) _ = SyncSharedFolderAsync(dev);
        EnsureSharedWatcher();
    }

    /// <summary>Notices devices closed from the emulator window itself (when we hold no process handle).</summary>
    private static async Task LivenessLoopAsync(RunningDevice dev)
    {
        var misses = 0;
        while (!dev.Cts.IsCancellationRequested && dev.State == DeviceState.Running)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), dev.Cts.Token); } catch (OperationCanceledException) { return; }
            if (dev.Process is { HasExited: false }) { misses = 0; continue; }
            misses = (await Adb.ListEmulatorsAsync()).Contains(dev.Serial) ? 0 : misses + 1;
            if (misses >= 3)
            {
                dev.State = DeviceState.Stopped;
                dev.Cts.Cancel();
                Devices.Remove(dev);
                return;
            }
        }
    }

    // ---------- PC Bridge companion app ----------

    private static async Task InstallBridgeAsync(RunningDevice dev)
    {
        var p = dev.Profile;
        if (!File.Exists(AppPaths.BridgeApk))
        {
            Log.Info("pcbridge.apk not bundled - skipping bridge install");
            return;
        }
        var version = UpdateService.CurrentVersion.ToString();
        var installed = (await Adb.ShellAsync(dev.Serial, "pm list packages com.emulate.pcbridge")).Output.Contains("com.emulate.pcbridge");
        if (installed && p.BridgeVersion == version) return;

        dev.Detail = "Installing PC Bridge";
        var r = await Adb.RunAsync(TimeSpan.FromMinutes(3), "-s", dev.Serial, "install", "-r", "-g", AppPaths.BridgeApk);
        if (!r.Ok || !r.Output.Contains("Success"))
        {
            // Signature changed between builds: reinstall from scratch (it holds no user data).
            await Adb.RunAsync(TimeSpan.FromSeconds(60), "-s", dev.Serial, "uninstall", "com.emulate.pcbridge");
            r = await Adb.RunAsync(TimeSpan.FromMinutes(3), "-s", dev.Serial, "install", "-g", AppPaths.BridgeApk);
        }
        if (r.Output.Contains("Success"))
        {
            p.BridgeVersion = version;
            ProfileStore.Save();
        }
        else
        {
            Log.Error("PC Bridge install failed: " + r.Combined);
        }
    }

    /// <summary>Hands the bridge app its secret token + port. Only the adb shell can deliver this broadcast.</summary>
    public static Task SendBridgeConfigAsync(RunningDevice dev) =>
        Adb.ShellAsync(dev.Serial,
            "am broadcast -n com.emulate.pcbridge/.ConfigReceiver -a com.emulate.pcbridge.CONFIG " +
            $"--es token {AppSettings.Current.BridgeToken} --ei port {BridgeServer.Port} --es dev {dev.Profile.Id}");

    // ---------- Location ----------

    private static async Task LocationLoopAsync(RunningDevice dev)
    {
        var ct = dev.Cts.Token;
        GeoFix? lastSent = null;
        var lastSentAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested && dev.State == DeviceState.Running)
        {
            try
            {
                var p = dev.Profile;
                GeoFix? fix = p.Location switch
                {
                    LocationMode.Windows => await LocationService.GetHostLocationAsync(allowWindows: true),
                    LocationMode.Ip => await LocationService.GetHostLocationAsync(allowWindows: false),
                    LocationMode.Manual => new GeoFix(p.ManualLatitude, p.ManualLongitude, 0, 1, "Manual"),
                    _ => null,
                };
                if (fix != null)
                {
                    // Re-send periodically even when unchanged: apps expect a steady GPS stream.
                    if (lastSent != fix || (DateTime.UtcNow - lastSentAt).TotalSeconds > 30)
                    {
                        await LocationService.SendToDeviceAsync(dev.Serial, fix);
                        lastSent = fix;
                        lastSentAt = DateTime.UtcNow;
                    }
                    dev.LocationText = "Location: " + fix;
                }
                else
                {
                    dev.LocationText = p.Location == LocationMode.Off ? "Location: off" : "Location: unavailable";
                }
            }
            catch (Exception ex)
            {
                Log.Info("Location update failed: " + ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public static async Task PushLocationNowAsync(RunningDevice dev)
    {
        var p = dev.Profile;
        var fix = p.Location switch
        {
            LocationMode.Manual => new GeoFix(p.ManualLatitude, p.ManualLongitude, 0, 1, "Manual"),
            LocationMode.Off => null,
            _ => await LocationService.GetHostLocationAsync(p.Location == LocationMode.Windows),
        };
        if (fix == null) return;
        await LocationService.SendToDeviceAsync(dev.Serial, fix);
        dev.LocationText = "Location: " + fix;
    }

    // ---------- Files ----------

    private const string DeviceDownloadFolder = "/sdcard/Download";

    /// <summary>Copies PC files onto the device. Photos/videos land in the gallery, everything else in Downloads.</summary>
    public static async Task<int> SendFilesAsync(RunningDevice dev, IEnumerable<string> files, string mediaFolder = "From PC")
    {
        var count = 0;
        foreach (var f in files)
        {
            var folder = Mime.IsMedia(f) ? "/sdcard/Pictures/" + mediaFolder : DeviceDownloadFolder;
            await Adb.ShellAsync(dev.Serial, $"mkdir -p {Adb.ShQuote(folder)}");
            var remote = $"{folder}/{Path.GetFileName(f)}";
            var r = await Adb.RunAsync(TimeSpan.FromMinutes(10), "-s", dev.Serial, "push", f, remote);
            if (!r.Ok) { Log.Error($"push {f} failed: {r.Combined}"); continue; }
            await ScanAsync(dev, remote);
            count++;
        }
        return count;
    }

    private static Task ScanAsync(RunningDevice dev, string remotePath) =>
        Adb.ShellAsync(dev.Serial,
            $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {Adb.ShQuote("file://" + remotePath)}");

    /// <summary>Mirrors the PC "Emulate Shared" folder into the device gallery (Pictures/Emulate Shared).</summary>
    public static async Task SyncSharedFolderAsync(RunningDevice dev)
    {
        try
        {
            var local = AppPaths.SharedFolder;
            Directory.CreateDirectory(local);
            var files = Directory.GetFiles(local, "*", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return;
            const string remoteDir = "/sdcard/Pictures/Emulate Shared";
            await Adb.ShellAsync(dev.Serial, $"mkdir -p {Adb.ShQuote(remoteDir)}");
            // "push --sync" only copies files that are new or changed.
            var args = new List<string> { "-s", dev.Serial, "push", "--sync" };
            args.AddRange(files);
            args.Add(remoteDir + "/");
            var r = await Adb.RunAsync(TimeSpan.FromMinutes(20), args.ToArray());
            if (r.Ok)
            {
                foreach (var f in files.Where(Mime.IsMedia)) await ScanAsync(dev, $"{remoteDir}/{Path.GetFileName(f)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Shared folder sync failed", ex);
        }
    }

    private static void EnsureSharedWatcher()
    {
        if (_sharedWatcher != null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.SharedFolder);
            _sharedWatcher = new FileSystemWatcher(AppPaths.SharedFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler h = (_, _) => DebouncedSharedSync();
            _sharedWatcher.Created += h;
            _sharedWatcher.Changed += h;
            _sharedWatcher.Renamed += (_, _) => DebouncedSharedSync();
            _sharedWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch shared folder", ex);
        }
    }

    private static void DebouncedSharedSync()
    {
        _sharedDebounce?.Cancel();
        var cts = _sharedDebounce = new CancellationTokenSource();
        _ = Task.Delay(2500, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            foreach (var d in Devices.ToList().Where(d => d.IsRunning && d.Profile.SyncSharedFolder))
                await SyncSharedFolderAsync(d);
        }, TaskScheduler.Default);
    }

    /// <summary>Copies the device's camera photos, screenshots and downloads to Downloads\Emulate\&lt;profile&gt;.</summary>
    public static async Task<string> PullMediaAsync(RunningDevice dev)
    {
        var dest = Path.Combine(AppPaths.FromDeviceFolder, MakeSafe(dev.Profile.Name));
        Directory.CreateDirectory(dest);
        foreach (var remote in new[] { "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Download", "/sdcard/Movies" })
            await Adb.RunAsync(TimeSpan.FromMinutes(20), "-s", dev.Serial, "pull", "-a", remote, dest);
        return dest;
    }

    public static async Task<string> ScreenshotAsync(RunningDevice dev)
    {
        var png = await ProcessRunner.RunBinaryAsync(AppPaths.Adb, new[] { "-s", dev.Serial, "exec-out", "screencap", "-p" }, TimeSpan.FromSeconds(30));
        if (png.Length < 100) throw new InvalidOperationException("Screenshot failed.");
        Directory.CreateDirectory(AppPaths.ScreenshotFolder);
        var file = Path.Combine(AppPaths.ScreenshotFolder, $"{MakeSafe(dev.Profile.Name)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        await File.WriteAllBytesAsync(file, png);
        return file;
    }

    public static async Task<string> InstallApkAsync(RunningDevice dev, string apk)
    {
        var r = await Adb.RunAsync(TimeSpan.FromMinutes(5), "-s", dev.Serial, "install", "-r", apk);
        return r.Output.Contains("Success") ? "Installed " + Path.GetFileName(apk) : "Install failed: " + r.Combined;
    }

    private static string MakeSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---------- Window + navigation ----------

    /// <summary>Waits for the emulator's window ("Android Emulator - avd:port") to appear.</summary>
    private static async Task FindWindowAsync(RunningDevice dev)
    {
        var prefix = $"Android Emulator - {dev.Profile.AvdName}:";
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline && !dev.Cts.IsCancellationRequested)
        {
            foreach (var w in Native.TopLevelWindows(visibleOnly: false))
            {
                var title = Native.GetTitle(w);
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Native.IsWindowVisible(w))
                {
                    dev.EmulatorWindow = w;
                    return;
                }
            }
            try { await Task.Delay(250, dev.Cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public const int KeyBack = 4, KeyHome = 3, KeyRecents = 187, KeyPower = 26, KeyVolumeUp = 24, KeyVolumeDown = 25;

    public static Task SendKeyAsync(RunningDevice dev, int keyCode) =>
        Adb.ShellAsync(dev.Serial, $"input keyevent {keyCode}", TimeSpan.FromSeconds(10));

    public static Task RotateAsync(RunningDevice dev) => Adb.EmuAsync(dev.Serial, "rotate");

    // ---------- Stop / re-attach ----------

    public static async Task StopAsync(RunningDevice dev)
    {
        if (dev.State is DeviceState.Failed or DeviceState.Stopped)
        {
            Devices.Remove(dev);
            return;
        }
        if (dev.State is DeviceState.Preparing or DeviceState.Starting && dev.Process == null)
        {
            dev.Cts.Cancel();
            Devices.Remove(dev);
            return;
        }
        dev.State = DeviceState.Stopping;
        dev.Detail = "Saving state";
        await Adb.EmuAsync(dev.Serial, "kill");
        for (var i = 0; i < 40; i++)
        {
            if (dev.Process?.HasExited ?? !(await Adb.ListEmulatorsAsync()).Contains(dev.Serial)) break;
            await Task.Delay(500);
        }
        if (dev.Process is { HasExited: false }) { try { dev.Process.Kill(true); } catch { } }
        dev.Cts.Cancel();
        dev.State = DeviceState.Stopped;
        Devices.Remove(dev);
    }

    public static void BringToFront(RunningDevice dev)
    {
        try
        {
            var handle = dev.EmulatorWindow != IntPtr.Zero ? dev.EmulatorWindow : dev.Process?.MainWindowHandle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero) Native.ShowWindow(handle, 9 /* SW_RESTORE */);
            if (handle != IntPtr.Zero) Native.SetForegroundWindow(handle);
        }
        catch
        {
            // Best effort only.
        }
    }

    /// <summary>After an update/restart, re-attach to emulators that are still running so nothing is lost.</summary>
    public static async Task ReattachAsync()
    {
        if (!File.Exists(AppPaths.Adb)) return;
        try
        {
            foreach (var serial in await Adb.ListEmulatorsAsync())
            {
                var avd = await Adb.GetAvdNameAsync(serial);
                var profile = ProfileStore.All.FirstOrDefault(p => p.AvdName == avd);
                if (profile == null || Find(profile) != null) continue;
                var dev = new RunningDevice(profile) { Port = int.Parse(serial["emulator-".Length..]), Docked = profile.LaunchDocked };
                dev.Process = FindEmulatorProcess(profile.AvdName);
                if (dev.Process != null)
                {
                    dev.Process.EnableRaisingEvents = true;
                    dev.Process.Exited += (_, _) =>
                    {
                        dev.State = DeviceState.Stopped;
                        dev.Cts.Cancel();
                        Application.Current?.Dispatcher.BeginInvoke(() => Devices.Remove(dev));
                    };
                }
                Devices.Add(dev);
                _ = FindWindowAsync(dev);
                Log.Info($"Re-attached to running device {profile.Name} ({serial})");
                await AfterBootAsync(dev);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Re-attach failed", ex);
        }
    }

    private static Process? FindEmulatorProcess(string avdName)
    {
        // The emulator front-end is "emulator.exe"; the actual VM is "qemu-system-x86_64*.exe".
        foreach (var name in new[] { "qemu-system-x86_64", "qemu-system-x86_64-headless", "emulator" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (proc.MainWindowTitle.Contains(avdName, StringComparison.OrdinalIgnoreCase)) return proc;
                }
                catch { }
            }
        }
        return null;
    }
}
