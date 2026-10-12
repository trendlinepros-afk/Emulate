using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Emulate.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, string PageUrl);

/// <summary>
/// Self-update from GitHub Releases. Every push builds a release (see .github/workflows/release.yml)
/// containing Emulate-win-x64.zip; this downloads it, swaps the files once the app has exited, and restarts.
/// </summary>
public static class UpdateService
{
    public const string Owner = "trendlinepros-afk";
    public const string Repo = "Emulate";
    public const string ZipAssetName = "Emulate-win-x64.zip";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static HttpClient Http => SdkInstaller.Http;

    /// <summary>Returns the newer release, or null when already up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("Emulate-Updater");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No releases have been published yet.");
        if ((int)resp.StatusCode == 403)
            throw new InvalidOperationException("GitHub rate limit reached. Try again in a few minutes.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest))
            throw new InvalidOperationException($"Latest release tag '{tag}' is not a version number.");

        string? zipUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), ZipAssetName, StringComparison.OrdinalIgnoreCase))
                zipUrl = asset.GetProperty("browser_download_url").GetString();
        }

        Log.Info($"Update check: current {CurrentVersion}, latest {latest} ({tag})");
        if (latest <= CurrentVersion) return null;
        if (zipUrl == null) throw new InvalidOperationException($"Release {tag} has no {ZipAssetName} attached yet. It may still be building.");

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(latest, tag, notes, zipUrl, page);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s.Contains('.') ? s : s + ".0", out var v))
        {
            version = Normalize(v);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Downloads the update, starts the swap script and shuts the app down. The script relaunches the new version.</summary>
    public static async Task DownloadAndRestartAsync(UpdateInfo info, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "Emulate-update", info.Tag);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var zip = Path.Combine(work, ZipAssetName);
        await SdkInstaller.DownloadFileAsync(info.ZipUrl, zip, 0, null, $"Emulate {info.Tag}", progress, ct);

        progress.Report(new DownloadProgress("Unpacking update", 0, 0));
        var staged = Path.Combine(work, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged), ct);

        // Find the folder that holds Emulate.exe (zip may or may not have a top folder).
        var exe = Directory.GetFiles(staged, "Emulate.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("The downloaded update does not contain Emulate.exe.");
        var source = Path.GetDirectoryName(exe)!;
        var target = AppPaths.InstallDir.TrimEnd('\\', '/');

        RestartWithSwap(source, target, info.Version.ToString());
    }

    /// <summary>Plain restart (used by Settings > Restart app).</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.InstallDir, "Emulate.exe");
        var script = Path.Combine(Path.GetTempPath(), "emulate-restart.ps1");
        File.WriteAllText(script, $$"""
            param([int]$ParentPid)
            try { Wait-Process -Id $ParentPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
            Start-Process -FilePath '{{exe.Replace("'", "''")}}'
            """, Encoding.UTF8);
        StartPowerShell(script, $"-ParentPid {Environment.ProcessId}", elevated: false);
        App.ShutdownForUpdate();
    }

    private static void RestartWithSwap(string source, string target, string version)
    {
        var log = Path.Combine(AppPaths.LogDir, "update.log");
        Directory.CreateDirectory(AppPaths.LogDir);
        var script = Path.Combine(Path.GetTempPath(), "Emulate-update", "apply-update.ps1");
        File.WriteAllText(script, """
            param([int]$ParentPid, [string]$Source, [string]$Target, [string]$Log, [string]$Version)
            function Say($m) { Add-Content -Path $Log -Value ("{0:u} {1}" -f (Get-Date), $m) }
            Say "Waiting for Emulate (pid $ParentPid) to exit..."
            try { Wait-Process -Id $ParentPid -Timeout 90 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 800
            $ok = $false
            for ($i = 0; $i -lt 10 -and -not $ok; $i++) {
                & robocopy $Source $Target /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
                if ($LASTEXITCODE -lt 8) { $ok = $true } else { Say "robocopy exit $LASTEXITCODE, retrying"; Start-Sleep -Seconds 2 }
            }
            if ($ok) { Say "Updated to $Version" } else { Say "Update FAILED - starting the previous version" }
            $exe = Join-Path $Target 'Emulate.exe'
            if ($ok) { Start-Process -FilePath $exe -ArgumentList '--updated', $Version } else { Start-Process -FilePath $exe -ArgumentList '--update-failed' }
            """, Encoding.UTF8);

        var args = $"-ParentPid {Environment.ProcessId} -Source \"{source}\" -Target \"{target}\" -Log \"{log}\" -Version \"{version}\"";
        StartPowerShell(script, args, elevated: !CanWrite(target));
        Log.Info($"Update to {version} staged from {source}; shutting down to apply.");
        App.ShutdownForUpdate();
    }

    private static void StartPowerShell(string script, string args, bool elevated)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" {args}")
        {
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevated) psi.Verb = "runas";
        Process.Start(psi);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
