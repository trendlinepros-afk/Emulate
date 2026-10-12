using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Emulate.Models;
using Emulate.Services;

namespace Emulate.Views;

public partial class ProfileWindow : Window
{
    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private readonly bool _isNew;
    public Profile Profile { get; }

    public ProfileWindow(Profile? existing, OsDefinition os, bool locationOnly = false)
    {
        InitializeComponent();
        _isNew = existing == null;
        Profile = existing ?? new Profile
        {
            OsId = os.Id,
            DeviceTypeId = os.DefaultDevice,
            RamMb = os.DefaultRamMb,
            StorageGb = os.Preset == OsPreset.Lite ? 8 : 16,
            CpuCores = os.Preset == OsPreset.Lite ? 2 : 4,
            Name = SuggestName(os),
        };

        Title = _isNew ? $"New {os.Name} profile" : $"Edit {Profile.Name}";
        HeaderText.Text = _isNew ? $"New {os.Name} profile" : (locationOnly ? $"Location for {Profile.Name}" : $"Edit {Profile.Name}");
        SaveButton.Content = _isNew ? "Create profile" : "Save";
        if (locationOnly) GeneralSection.Visibility = Visibility.Collapsed;

        NameBox.Text = Profile.Name;
        DeviceTypeList.ItemsSource = DeviceTypes.All;
        DeviceTypeList.SelectedItem = Profile.Device;

        var ram = new[] { 2048, 3072, 4096, 6144, 8192 }.Select(m => new Choice<int>($"{m / 1024.0:0.#} GB", m)).ToList();
        RamList.ItemsSource = ram;
        RamList.SelectedItem = ram.OrderBy(c => Math.Abs(c.Value - Profile.RamMb)).First();

        var storage = new[] { 8, 16, 32, 64 }.Select(g => new Choice<int>($"{g} GB", g)).ToList();
        StorageList.ItemsSource = storage;
        StorageList.SelectedItem = storage.OrderBy(c => Math.Abs(c.Value - Profile.StorageGb)).First();

        var cams = new List<Choice<string>>
        {
            new("PC webcam", "webcam0"),
            new("Simulated camera", "emulated"),
            new("No camera", "none"),
        };
        CameraList.ItemsSource = cams;
        CameraList.SelectedItem = cams.FirstOrDefault(c => c.Value == Profile.Camera) ?? cams[0];

        SyncBox.IsChecked = Profile.SyncSharedFolder;

        var locs = new List<Choice<LocationMode>>
        {
            new("My real location", LocationMode.Windows),
            new("Approximate (from IP)", LocationMode.Ip),
            new("Pick a place", LocationMode.Manual),
            new("Off", LocationMode.Off),
        };
        LocationList.ItemsSource = locs;
        LocationList.SelectedItem = locs.First(c => c.Value == Profile.Location);
        LatBox.Text = Profile.ManualLatitude.ToString("0.######", CultureInfo.InvariantCulture);
        LonBox.Text = Profile.ManualLongitude.ToString("0.######", CultureInfo.InvariantCulture);

        Loaded += async (_, _) =>
        {
            if (_isNew) { NameBox.Focus(); NameBox.SelectAll(); }
            var fix = await LocationService.GetHostLocationAsync();
            CurrentLocation.Text = fix != null ? "This PC's location right now: " + fix : "Could not determine this PC's location.";
            if (LocationService.LastWindowsError != null) CurrentLocation.Text += "\n" + LocationService.LastWindowsError;
        };
    }

    private static string SuggestName(OsDefinition os)
    {
        var baseName = os.Preset switch
        {
            OsPreset.Hardened => "Private phone",
            OsPreset.Lite => "Lite phone",
            _ => "My phone",
        };
        var existing = ProfileStore.All.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!existing.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    private void LocationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = (LocationList.SelectedItem as Choice<LocationMode>)?.Value ?? LocationMode.Windows;
        ManualPanel.Visibility = mode == LocationMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        LocationHelp.Text = mode switch
        {
            LocationMode.Windows => "Uses Windows location services (GPS / Wi-Fi positioning) and keeps the device updated live. Falls back to your IP if Windows location is off.",
            LocationMode.Ip => "Uses your internet connection's approximate city-level location.",
            LocationMode.Manual => "The device will report this exact spot.",
            _ => "No location is sent to the device.",
        };
    }

    private async void UseMyLocation_Click(object sender, RoutedEventArgs e)
    {
        var fix = await LocationService.GetHostLocationAsync();
        if (fix == null) return;
        LatBox.Text = fix.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        LonBox.Text = fix.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool TryParseCoord(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Please give the profile a name.", "Emulate");
            return;
        }

        var mode = (LocationList.SelectedItem as Choice<LocationMode>)?.Value ?? LocationMode.Windows;
        if (mode == LocationMode.Manual)
        {
            // Allow pasting "lat, lon" straight from Google Maps into the latitude box.
            var latText = LatBox.Text;
            var lonText = LonBox.Text;
            var parts = latText.Split(',');
            if (parts.Length == 2) { latText = parts[0]; lonText = parts[1]; }
            if (!TryParseCoord(latText, out var lat) || !TryParseCoord(lonText, out var lon) ||
                lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                MessageBox.Show(this, "Enter a valid latitude (-90..90) and longitude (-180..180).", "Emulate");
                return;
            }
            Profile.ManualLatitude = lat;
            Profile.ManualLongitude = lon;
        }

        Profile.Name = name;
        Profile.DeviceTypeId = (DeviceTypeList.SelectedItem as DeviceType)?.Id ?? Profile.DeviceTypeId;
        Profile.RamMb = (RamList.SelectedItem as Choice<int>)?.Value ?? Profile.RamMb;
        var storage = (StorageList.SelectedItem as Choice<int>)?.Value ?? Profile.StorageGb;
        if (!_isNew && storage < Profile.StorageGb) Log.Say("Storage can only be increased on an existing device.");
        // Shrinking the data partition of an existing device would corrupt it.
        Profile.StorageGb = _isNew ? storage : Math.Max(Profile.StorageGb, storage);
        Profile.Camera = (CameraList.SelectedItem as Choice<string>)?.Value ?? Profile.Camera;
        Profile.SyncSharedFolder = SyncBox.IsChecked == true;
        Profile.Location = mode;
        DialogResult = true;
    }
}
