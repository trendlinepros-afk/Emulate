using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace Emulate.Services;

public sealed record GeoFix(double Latitude, double Longitude, double Altitude, double AccuracyMeters, string Source)
{
    public override string ToString() =>
        FormattableString.Invariant($"{Latitude:0.00000}, {Longitude:0.00000} (±{AccuracyMeters:0} m, {Source})");
}

/// <summary>Gets the PC's real location: Windows location services first, public-IP lookup as fallback.</summary>
public static class LocationService
{
    private static GeoFix? _cached;
    private static DateTime _cachedAt;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? LastWindowsError { get; private set; }

    public static async Task<GeoFix?> GetHostLocationAsync(bool allowWindows = true)
    {
        await Gate.WaitAsync();
        try
        {
            if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 20 &&
                (allowWindows || _cached.Source != "Windows")) return _cached;

            GeoFix? fix = allowWindows ? await FromWindowsAsync() : null;
            fix ??= await FromIpAsync();
            if (fix != null)
            {
                _cached = fix;
                _cachedAt = DateTime.UtcNow;
            }
            return fix;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<GeoFix?> FromWindowsAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                LastWindowsError = "Windows location access is off. Turn on Settings > Privacy & security > Location " +
                                   "(and 'Let desktop apps access your location').";
                return null;
            }
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15));
            var p = pos.Coordinate.Point.Position;
            LastWindowsError = null;
            return new GeoFix(p.Latitude, p.Longitude, double.IsNaN(p.Altitude) ? 0 : p.Altitude,
                pos.Coordinate.Accuracy, "Windows");
        }
        catch (Exception ex)
        {
            LastWindowsError = "Windows location unavailable: " + ex.Message;
            Log.Info(LastWindowsError);
            return null;
        }
    }

    private static async Task<GeoFix?> FromIpAsync()
    {
        var sources = new (string url, string lat, string lon)[]
        {
            ("https://ipwho.is/", "latitude", "longitude"),
            ("https://ipapi.co/json/", "latitude", "longitude"),
            ("https://freeipapi.com/api/json", "latitude", "longitude"),
        };
        foreach (var (url, latKey, lonKey) in sources)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var json = await SdkInstaller.Http.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(latKey, out var lat) && doc.RootElement.TryGetProperty(lonKey, out var lon) &&
                    lat.ValueKind == JsonValueKind.Number && lon.ValueKind == JsonValueKind.Number)
                {
                    return new GeoFix(lat.GetDouble(), lon.GetDouble(), 0, 5000, "IP address");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"IP location via {url} failed: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Pushes a GPS fix into the emulator. Note: the emulator console takes longitude first.</summary>
    public static Task SendToDeviceAsync(string serial, GeoFix fix) =>
        Adb.EmuAsync(serial, "geo", "fix",
            fix.Longitude.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            fix.Latitude.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            fix.Altitude.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
}
