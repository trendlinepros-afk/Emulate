using System.Text.Json;
using Emulate.Services;

namespace Emulate.Models;

public enum LocationMode
{
    Windows,   // live location from Windows location services (falls back to IP)
    Ip,        // approximate location from the public IP address
    Manual,    // fixed coordinates
    Off,
}

/// <summary>A profile is one emulated device (an AVD) running one of the catalog OSes.</summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "My phone";
    public string OsId { get; set; } = OsCatalog.All[0].Id;
    public string DeviceTypeId { get; set; } = "standard";
    public int RamMb { get; set; } = 4096;
    public int StorageGb { get; set; } = 16;
    public int CpuCores { get; set; } = 4;
    public string Camera { get; set; } = "webcam0"; // webcam0 | emulated | none
    public LocationMode Location { get; set; } = LocationMode.Windows;
    public double ManualLatitude { get; set; } = 40.7580;
    public double ManualLongitude { get; set; } = -73.9855;
    public bool SyncSharedFolder { get; set; } = true;
    /// <summary>Remembered launch mode: true = inside the Emulate window, false = its own window.</summary>
    public bool LaunchDocked { get; set; } = true;
    public bool PresetApplied { get; set; }
    public string? BridgeVersion { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastUsed { get; set; }

    public string AvdName => "emulate_" + Id;
    public OsDefinition Os => OsCatalog.Find(OsId) ?? OsCatalog.All[0];
    public DeviceType Device => DeviceTypes.Find(DeviceTypeId);
}

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static List<Profile>? _profiles;

    public static List<Profile> All
    {
        get
        {
            lock (Gate)
            {
                if (_profiles == null)
                {
                    try
                    {
                        _profiles = File.Exists(AppPaths.ProfilesFile)
                            ? JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(AppPaths.ProfilesFile), Json) ?? new()
                            : new();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not read profiles", ex);
                        _profiles = new();
                    }
                }
                return _profiles;
            }
        }
    }

    public static IEnumerable<Profile> ForOs(string osId) => All.Where(p => p.OsId == osId).OrderByDescending(p => p.LastUsed ?? p.Created);

    public static void Add(Profile p)
    {
        lock (Gate) All.Add(p);
        Save();
    }

    public static void Remove(Profile p)
    {
        lock (Gate) All.Remove(p);
        Save();
    }

    public static void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var tmp = AppPaths.ProfilesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, Json));
            File.Move(tmp, AppPaths.ProfilesFile, true);
        }
    }
}
