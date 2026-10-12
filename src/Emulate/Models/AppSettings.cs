using System.Security.Cryptography;
using System.Text.Json;
using Emulate.Services;

namespace Emulate.Models;

public sealed class AppSettings
{
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool AcceptedSdkLicense { get; set; }
    public string? SdkRootOverride { get; set; }
    public string? AvdRootOverride { get; set; }
    public string? SharedFolderOverride { get; set; }
    public string BridgeToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public int BridgePort { get; set; } = 47823;
    public string? SkippedVersion { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings", ex);
        }
        var fresh = new AppSettings();
        _current = fresh;
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
