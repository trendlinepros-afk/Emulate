using Emulate.Models;

namespace Emulate.Services;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Emulate - settings, profiles, logs. Lives outside the install folder so updates never touch it.</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emulate");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ProfilesFile => Path.Combine(DataRoot, "profiles.json");
    public static string LogDir => Path.Combine(DataRoot, "logs");

    public static string SdkRoot => Nonempty(AppSettings.Current.SdkRootOverride) ?? Path.Combine(DataRoot, "sdk");
    public static string AvdRoot => Nonempty(AppSettings.Current.AvdRootOverride) ?? Path.Combine(DataRoot, "avd");
    /// <summary>Plays the role of %USERPROFILE%\.android (adb keys, emulator prefs) so we never collide with Android Studio.</summary>
    public static string AndroidUserHome => Path.Combine(DataRoot, "android-home");
    public static string DownloadCache => Path.Combine(DataRoot, "downloads");

    public static string Adb => Path.Combine(SdkRoot, "platform-tools", "adb.exe");
    public static string Emulator => Path.Combine(SdkRoot, "emulator", "emulator.exe");

    public static string SharedFolder => Nonempty(AppSettings.Current.SharedFolderOverride) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Emulate Shared");

    public static string ScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Emulate Screenshots");

    public static string FromDeviceFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Emulate");

    public static string InstallDir => AppContext.BaseDirectory;
    public static string BridgeApk => Path.Combine(InstallDir, "pcbridge.apk");

    private static string? Nonempty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Environment every SDK tool is started with, so everything stays inside our own folders.</summary>
    public static void ApplyAndroidEnvironment(IDictionary<string, string?> env)
    {
        env["ANDROID_HOME"] = SdkRoot;
        env["ANDROID_SDK_ROOT"] = SdkRoot;
        env["ANDROID_AVD_HOME"] = AvdRoot;
        env["ANDROID_USER_HOME"] = AndroidUserHome;
        env["ANDROID_EMULATOR_HOME"] = AndroidUserHome;
    }
}
