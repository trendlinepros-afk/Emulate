using System.Text;
using Emulate.Models;

namespace Emulate.Services;

/// <summary>Creates/updates the AVD files (the emulator's device definition) for a profile.</summary>
public static class AvdWriter
{
    public static string AvdDir(Profile p) => Path.Combine(AppPaths.AvdRoot, p.AvdName + ".avd");
    public static string AvdIni(Profile p) => Path.Combine(AppPaths.AvdRoot, p.AvdName + ".ini");

    /// <summary>Writes the AVD config. Safe to call before every boot: user data lives in separate image files.</summary>
    public static void Write(Profile p)
    {
        var os = p.Os;
        var device = p.Device;
        var dir = AvdDir(p);
        Directory.CreateDirectory(dir);

        File.WriteAllText(AvdIni(p), string.Join("\r\n",
            "avd.ini.encoding=UTF-8",
            $"path={dir}",
            $"path.rel=avd\\{p.AvdName}.avd",
            $"target=android-{os.Api}",
            ""), new UTF8Encoding(false));

        var cfg = new Dictionary<string, string>
        {
            ["AvdId"] = p.AvdName,
            ["avd.ini.displayname"] = Sanitize(p.Name),
            ["avd.ini.encoding"] = "UTF-8",
            ["abi.type"] = OsDefinition.Abi,
            ["hw.cpu.arch"] = "x86_64",
            ["hw.cpu.ncore"] = p.CpuCores.ToString(),
            ["image.sysdir.1"] = os.SystemImageRelativeDir.Replace('/', '\\') + "\\",
            ["image.androidVersion.api"] = os.Api.ToString(),
            ["tag.id"] = OsDefinition.Tag,
            ["tag.display"] = "Google Play",
            ["tag.ids"] = OsDefinition.Tag,
            ["tag.displaynames"] = "Google Play",
            ["PlayStore.enabled"] = "true",
            ["hw.ramSize"] = $"{p.RamMb}M",
            ["vm.heapSize"] = p.RamMb >= 4096 ? "512M" : "256M",
            ["disk.dataPartition.size"] = $"{p.StorageGb}G",
            ["hw.lcd.width"] = device.Width.ToString(),
            ["hw.lcd.height"] = device.Height.ToString(),
            ["hw.lcd.density"] = device.Density.ToString(),
            ["hw.initialOrientation"] = device.Tablet ? "landscape" : "portrait",
            ["skin.dynamic"] = "yes",
            ["skin.name"] = $"{device.Width}x{device.Height}",
            ["skin.path"] = "_no_skin",
            ["showDeviceFrame"] = "no",
            ["hw.mainKeys"] = "no",
            ["hw.keyboard"] = "yes",
            ["hw.dPad"] = "no",
            ["hw.trackBall"] = "no",
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = "auto",
            ["hw.gps"] = "yes",
            ["hw.gsmModem"] = "yes",
            ["hw.battery"] = "yes",
            ["hw.accelerometer"] = "yes",
            ["hw.gyroscope"] = "yes",
            ["hw.sensors.orientation"] = "yes",
            ["hw.sensors.proximity"] = "yes",
            ["hw.sensors.light"] = "yes",
            ["hw.audioInput"] = "yes",
            ["hw.audioOutput"] = "yes",
            ["hw.camera.back"] = p.Camera == "none" ? "none" : p.Camera == "emulated" ? "virtualscene" : Webcam(p.Camera),
            ["hw.camera.front"] = p.Camera == "none" ? "none" : p.Camera == "emulated" ? "emulated" : Webcam(p.Camera),
            ["hw.sdCard"] = "no",
            ["fastboot.forceColdBoot"] = "no",
            ["fastboot.forceFastBoot"] = "yes",
            ["firstboot.bootFromDownloadableSnapshot"] = "no",
            ["hw.arc"] = "false",
            ["runtime.network.latency"] = "none",
            ["runtime.network.speed"] = "full",
        };

        var sb = new StringBuilder();
        foreach (var kv in cfg) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
        File.WriteAllText(Path.Combine(dir, "config.ini"), sb.ToString(), new UTF8Encoding(false));
    }

    public static void Delete(Profile p)
    {
        try { if (File.Exists(AvdIni(p))) File.Delete(AvdIni(p)); } catch (Exception ex) { Log.Error("Delete AVD ini", ex); }
        try { if (Directory.Exists(AvdDir(p))) Directory.Delete(AvdDir(p), true); } catch (Exception ex) { Log.Error("Delete AVD dir", ex); }
    }

    /// <summary>Wipes user data so the device boots like new (keeps the profile settings).</summary>
    public static void Wipe(Profile p)
    {
        var dir = AvdDir(p);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
        {
            if (Path.GetFileName(f).Equals("config.ini", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch (Exception ex) { Log.Error("Wipe " + f, ex); }
        }
        foreach (var d in Directory.GetDirectories(dir))
        {
            try { Directory.Delete(d, true); } catch (Exception ex) { Log.Error("Wipe " + d, ex); }
        }
        p.PresetApplied = false;
        p.BridgeVersion = null;
        ProfileStore.Save();
    }

    private static string Webcam(string camera) =>
        System.Text.RegularExpressions.Regex.IsMatch(camera, @"^webcam\d+$") ? camera : "webcam0";

    private static string Sanitize(string s) => new string(s.Where(c => c >= 32 && c < 127 && c != '=').ToArray());
}
