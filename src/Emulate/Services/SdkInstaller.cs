using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Xml.Linq;
using Emulate.Models;

namespace Emulate.Services;

public sealed record DownloadProgress(string Label, long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;

    public override string ToString() => Total > 0
        ? $"{Label} - {Received / 1048576.0:0} / {Total / 1048576.0:0} MB ({Fraction:P0})"
        : $"{Label} - {Received / 1048576.0:0} MB";
}

/// <summary>
/// Installs the pieces of the Android SDK we need (emulator, platform-tools, Google Play system images)
/// straight from Google's official SDK repository, without needing Java/sdkmanager or Android Studio.
/// </summary>
public static class SdkInstaller
{
    private const string RepoBase = "https://dl.google.com/android/repository/";
    private static readonly string[] RepoManifests = { "repository2-3.xml", "repository2-2.xml", "repository2-1.xml" };
    private static readonly string[] SysImgManifests = { "sys-img2-3.xml", "sys-img2-2.xml", "sys-img2-1.xml" };

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Emulate/" + UpdateService.CurrentVersion);
        return h;
    }

    public static bool BaseToolsInstalled => File.Exists(AppPaths.Adb) && File.Exists(AppPaths.Emulator);

    public static bool IsImageInstalled(OsDefinition os) =>
        File.Exists(Path.Combine(AppPaths.SdkRoot, os.SystemImageRelativeDir, "system.img")) ||
        File.Exists(Path.Combine(AppPaths.SdkRoot, os.SystemImageRelativeDir, "source.properties"));

    public static async Task EnsureBaseToolsAsync(IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            // The emulator refuses some setups without these folders present.
            Directory.CreateDirectory(Path.Combine(AppPaths.SdkRoot, "platforms"));
            Directory.CreateDirectory(AppPaths.AvdRoot);
            Directory.CreateDirectory(AppPaths.AndroidUserHome);

            if (File.Exists(AppPaths.Adb) && File.Exists(AppPaths.Emulator)) return;

            var repo = await LoadManifestAsync(RepoBase, RepoManifests, ct);
            if (!File.Exists(AppPaths.Adb))
                await InstallPackageAsync(repo, RepoBase, "platform-tools", Path.Combine(AppPaths.SdkRoot, "platform-tools"), "Android platform tools", progress, ct);
            if (!File.Exists(AppPaths.Emulator))
                await InstallPackageAsync(repo, RepoBase, "emulator", Path.Combine(AppPaths.SdkRoot, "emulator"), "Android Emulator", progress, ct);
        }
        finally
        {
            InstallLock.Release();
        }
    }

    public static async Task EnsureSystemImageAsync(OsDefinition os, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (IsImageInstalled(os)) return;
            var baseUrl = $"{RepoBase}sys-img/{OsDefinition.Tag}/";
            var manifest = await LoadManifestAsync(baseUrl, SysImgManifests, ct);
            await InstallPackageAsync(manifest, baseUrl, os.SystemImagePackage,
                Path.Combine(AppPaths.SdkRoot, os.SystemImageRelativeDir), $"{os.Version} system image", progress, ct);
        }
        finally
        {
            InstallLock.Release();
        }
    }

    /// <summary>Deletes emulator + platform-tools so they are re-downloaded fresh on next launch.</summary>
    public static void ResetBaseTools()
    {
        foreach (var dir in new[] { "emulator", "platform-tools" })
        {
            var path = Path.Combine(AppPaths.SdkRoot, dir);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static async Task<XDocument> LoadManifestAsync(string baseUrl, string[] names, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var name in names)
        {
            try
            {
                var xml = await Http.GetStringAsync(baseUrl + name, ct);
                return XDocument.Parse(xml);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("Could not reach Google's Android SDK repository. Check your internet connection.", last);
    }

    private sealed record Archive(string Url, long Size, string? Sha1);

    private static Archive FindArchive(XDocument doc, string path)
    {
        var channels = doc.Descendants().Where(e => e.Name.LocalName == "channel")
            .ToDictionary(e => (string?)e.Attribute("id") ?? "", e => e.Value.Trim());

        var candidates = doc.Descendants()
            .Where(e => e.Name.LocalName == "remotePackage" && (string?)e.Attribute("path") == path)
            .Select(pkg =>
            {
                var channelRef = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "channelRef")?.Attribute("ref")?.Value ?? "channel-0";
                var stable = channels.TryGetValue(channelRef, out var c) ? c == "stable" : channelRef == "channel-0";
                var rev = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "revision");
                int Part(string n) => int.TryParse(rev?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value, out var v) ? v : 0;
                var version = new Version(Part("major"), Part("minor"), Part("micro"));
                var archive = pkg.Descendants().Where(e => e.Name.LocalName == "archive").FirstOrDefault(a =>
                {
                    var os = a.Elements().FirstOrDefault(e => e.Name.LocalName == "host-os")?.Value;
                    var arch = a.Elements().FirstOrDefault(e => e.Name.LocalName == "host-arch")?.Value;
                    return (os == null || os == "windows") && (arch == null || arch == "x64");
                });
                return (stable, version, archive);
            })
            .Where(c => c.archive != null)
            .OrderByDescending(c => c.stable)
            .ThenByDescending(c => c.version)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException($"Google's SDK repository has no Windows download for '{path}'.");

        var complete = candidates[0].archive!.Descendants().First(e => e.Name.LocalName == "complete");
        string? Child(string n) => complete.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim();
        var sha = complete.Elements().FirstOrDefault(e => e.Name.LocalName == "checksum");
        return new Archive(Child("url")!, long.TryParse(Child("size"), out var size) ? size : 0,
            sha != null && ((string?)sha.Attribute("type") ?? "sha1") == "sha1" ? sha.Value.Trim() : null);
    }

    private static async Task InstallPackageAsync(XDocument manifest, string baseUrl, string path, string targetDir, string label,
        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var archive = FindArchive(manifest, path);
        var url = archive.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? archive.Url : baseUrl + archive.Url;
        Log.Info($"Installing {path} from {url}");

        Directory.CreateDirectory(AppPaths.DownloadCache);
        var zipPath = Path.Combine(AppPaths.DownloadCache, Path.GetFileName(new Uri(url).LocalPath));
        await DownloadFileAsync(url, zipPath, archive.Size, archive.Sha1, label, progress, ct);

        progress.Report(new DownloadProgress($"Unpacking {label}", 0, 0));
        var staging = targetDir + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, staging), ct);

        // Zips contain a single top folder (e.g. "emulator/", "x86_64/") - unwrap it.
        var root = staging;
        var dirs = Directory.GetDirectories(staging);
        if (dirs.Length == 1 && Directory.GetFiles(staging).Length == 0) root = dirs[0];

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        Directory.Move(root, targetDir);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try { File.Delete(zipPath); } catch { }
        Log.Info($"Installed {path} into {targetDir}");
    }

    public static async Task DownloadFileAsync(string url, string dest, long expectedSize, string? sha1, string label,
        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (File.Exists(dest) && sha1 != null && await Sha1Async(dest, ct) == sha1.ToLowerInvariant()) return;

        var partial = dest + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buffer = new byte[1 << 20];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new DownloadProgress($"Downloading {label}", received, total));
                }
            }
        }

        if (sha1 != null)
        {
            progress.Report(new DownloadProgress($"Verifying {label}", 0, 0));
            var actual = await Sha1Async(partial, ct);
            if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidOperationException($"Download of {label} was corrupted (checksum mismatch). Please try again.");
            }
        }
        File.Move(partial, dest, true);
    }

    private static async Task<string> Sha1Async(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA1.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    /// <summary>The PC's cameras as the emulator sees them: ("webcam0", "Integrated Camera"), ...</summary>
    public static async Task<List<(string id, string name)>> ListWebcamsAsync()
    {
        var list = new List<(string, string)>();
        if (!File.Exists(AppPaths.Emulator)) return list;
        try
        {
            var r = await ProcessRunner.RunAsync(AppPaths.Emulator, new[] { "-webcam-list" }, TimeSpan.FromSeconds(20));
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r.Combined,
                         @"Camera '(webcam\d+)' is connected to device '([^']*)'"))
                list.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        catch (Exception ex)
        {
            Log.Info("webcam list: " + ex.Message);
        }
        return list;
    }

    /// <summary>Runs "emulator -accel-check" and reports whether hardware acceleration (WHPX/AEHD) is usable.</summary>
    public static async Task<(bool ok, string details)> CheckAccelerationAsync()
    {
        if (!File.Exists(AppPaths.Emulator)) return (false, "The Android Emulator is not installed yet. It downloads automatically on first launch.");
        var r = await ProcessRunner.RunAsync(AppPaths.Emulator, new[] { "-accel-check" }, TimeSpan.FromSeconds(60));
        return (r.Ok, r.Combined);
    }
}
