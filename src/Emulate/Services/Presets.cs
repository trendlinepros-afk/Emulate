using Emulate.Models;

namespace Emulate.Services;

/// <summary>One-time settings applied after a device's first boot, depending on the OS edition.</summary>
public static class Presets
{
    // Applied to every device: location on, automatic time zone.
    private static readonly string[] Common =
    {
        "settings put secure location_mode 3",
        "cmd location set-location-enabled true",
        "settings put global auto_time 1",
        "settings put global auto_time_zone 1",
    };

    private static readonly string[] Hardened =
    {
        // Encrypted DNS through Quad9 (malware-blocking resolver).
        "settings put global private_dns_mode hostname",
        "settings put global private_dns_specifier dns.quad9.net",
        // No background Wi-Fi / Bluetooth scanning for location.
        "settings put global wifi_scan_always_enabled 0",
        "settings put global ble_scan_always_enabled 0",
        "settings put global network_recommendations_enabled 0",
        // Play Protect on, and also scan apps installed over adb.
        "settings put global package_verifier_enable 1",
        "settings put global verifier_verify_adb_installs 1",
        // Hide notification contents on the lock screen.
        "settings put secure lock_screen_allow_private_notifications 0",
        // Don't send crash reports / usage.
        "settings put secure send_action_app_error 0",
        "settings put global upload_apk_enable 0",
    };

    private static readonly string[] HardenedRemove =
    {
        "com.google.android.youtube",
        "com.google.android.apps.youtube.music",
        "com.google.android.videos",
        "com.google.android.apps.maps",
        "com.google.android.apps.docs",
        "com.google.android.apps.tachyon",
        "com.google.android.apps.photos",
        "com.google.android.apps.subscriptions.red",
        "com.google.android.apps.wellbeing",
        "com.google.android.feedback",
        "com.google.android.apps.googleassistant",
        "com.google.android.apps.magazines",
        "com.google.android.apps.podcasts",
    };

    private static readonly string[] Lite =
    {
        "settings put global window_animation_scale 0.5",
        "settings put global transition_animation_scale 0.5",
        "settings put global animator_duration_scale 0.5",
        "settings put global wifi_scan_always_enabled 0",
        "settings put global ble_scan_always_enabled 0",
    };

    private static readonly string[] LiteRemove = HardenedRemove.Concat(new[]
    {
        "com.google.android.gm",
        "com.google.android.calendar",
        "com.google.android.apps.nbu.files.extras",
        "com.google.android.apps.restore",
        "com.google.android.apps.safetyhub",
        "com.google.android.apps.turbo",
        "com.google.android.apps.chromecast.app",
        "com.google.android.apps.youtube.kids",
        "com.google.android.keep",
        "com.google.android.apps.fitness",
        "com.google.android.apps.walletnfcrel",
        "com.google.android.apps.recorder",
        "com.google.android.apps.googleone",
    }).ToArray();

    public static async Task ApplyAsync(string serial, Profile profile)
    {
        var os = profile.Os;
        var commands = new List<string>(Common);
        string[] remove = Array.Empty<string>();
        switch (os.Preset)
        {
            case OsPreset.Hardened:
                commands.AddRange(Hardened);
                remove = HardenedRemove;
                break;
            case OsPreset.Lite:
                commands.AddRange(Lite);
                remove = LiteRemove;
                break;
        }

        Log.Say($"{profile.Name}: applying {os.Preset} setup...");
        foreach (var c in commands) await Adb.ShellAsync(serial, c);

        if (remove.Length > 0)
        {
            var installed = (await Adb.ShellAsync(serial, "pm list packages")).Output.Replace("\r", "") + "\n";
            foreach (var pkg in remove)
            {
                if (!installed.Contains("package:" + pkg + "\n")) continue;
                // Removed for the user only - reversible with "cmd package install-existing <pkg>".
                await Adb.ShellAsync(serial, $"pm uninstall -k --user 0 {pkg}");
            }
        }

        profile.PresetApplied = true;
        ProfileStore.Save();
    }
}
