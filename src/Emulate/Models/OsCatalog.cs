namespace Emulate.Models;

/// <summary>What gets applied to a fresh device after its first boot.</summary>
public enum OsPreset
{
    Standard,
    Hardened,
    Lite,
}

/// <summary>
/// One of the bootable Android "operating systems". All of them are official Google Play
/// system images (so the Play Store works); the secure/stripped editions add a hardening or
/// debloat preset on top of the stock image.
/// </summary>
public sealed record OsDefinition(
    string Id,
    string Name,
    string Version,
    int Api,
    string Category,
    string Tagline,
    string Description,
    OsPreset Preset,
    string DefaultDevice,
    int DefaultRamMb,
    string Accent)
{
    public const string Tag = "google_apis_playstore";
    public const string Abi = "x86_64";

    public string SystemImagePackage => $"system-images;android-{Api};{Tag};{Abi}";
    public string SystemImageRelativeDir => Path.Combine("system-images", $"android-{Api}", Tag, Abi);
    public string ApiLabel => $"API {Api}";
}

public static class OsCatalog
{
    public static readonly IReadOnlyList<OsDefinition> All = new[]
    {
        new OsDefinition(
            "android16-flagship", "Android 16 Flagship", "Android 16", 36, "Mainstream",
            "Newest Android, big flagship screen",
            "The latest Android release with Google Play, on a large 1440p flagship-class display " +
            "like a current Galaxy S Ultra. Best for trying the newest apps and features.",
            OsPreset.Standard, "flagship", 4096, "#4F8CFF"),

        new OsDefinition(
            "android15-mainstream", "Android 15 Everyday", "Android 15", 35, "Mainstream",
            "Popular, polished, great app support",
            "Android 15 with Google Play on a standard 6.2\" phone. The everyday choice: what most " +
            "current phones run, with excellent app compatibility.",
            OsPreset.Standard, "standard", 4096, "#34C38F"),

        new OsDefinition(
            "android14-stable", "Android 14 Stable", "Android 14", 34, "Mainstream",
            "Rock-solid, maximum compatibility",
            "Android 14 with Google Play. A mature, well-tested release for apps that are picky " +
            "about newer Android versions (banking, older games, work apps).",
            OsPreset.Standard, "standard", 3072, "#F2A93B"),

        new OsDefinition(
            "android15-hardened", "Android 15 Hardened", "Android 15", 35, "Secure",
            "Privacy-first, locked-down settings",
            "Android 15 with Google Play plus a privacy/security preset: encrypted Private DNS " +
            "(Quad9), background Wi-Fi/Bluetooth scanning off, Play Protect forced on, lock-screen " +
            "notification content hidden and Google extras (YouTube, Maps, Drive, Meet, etc.) removed.",
            OsPreset.Hardened, "standard", 4096, "#E05260"),

        new OsDefinition(
            "android13-lite", "Android 13 Lite", "Android 13", 33, "Secure",
            "Stripped-down, light and fast",
            "Android 13 with Google Play, debloated down to the essentials (Play Store, Chrome, " +
            "Phone, Messages, Camera, Files), faster animations and a small memory footprint. " +
            "Great for running several devices at once.",
            OsPreset.Lite, "compact", 2048, "#9B6CFF"),
    };

    public static OsDefinition? Find(string id) => All.FirstOrDefault(o => o.Id == id);
}

public sealed record DeviceType(string Id, string Name, int Width, int Height, int Density, bool Tablet)
{
    public override string ToString() => Name;
}

public static class DeviceTypes
{
    public static readonly IReadOnlyList<DeviceType> All = new[]
    {
        new DeviceType("flagship", "Flagship phone - 6.8\" 1440x3120", 1440, 3120, 560, false),
        new DeviceType("standard", "Standard phone - 6.2\" 1080x2400", 1080, 2400, 420, false),
        new DeviceType("compact", "Compact phone - 5.8\" 1080x2340", 1080, 2340, 440, false),
        new DeviceType("tablet", "Tablet - 11\" 1600x2560", 1600, 2560, 320, true),
    };

    public static DeviceType Find(string id) => All.FirstOrDefault(d => d.Id == id) ?? All[1];
}
