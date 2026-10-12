using System.Runtime.InteropServices;
using System.Text.Json;
using Emulate.Models;
using Microsoft.Win32.SafeHandles;

namespace Emulate.Services;

/// <summary>
/// Makes an exact copy of a profile: its settings plus the whole virtual disk (apps, accounts, photos, settings).
/// </summary>
public static class ProfileCloner
{
    private static readonly HashSet<string> Busy = new();

    public static bool IsBusy(Profile p)
    {
        lock (Busy) return Busy.Contains(p.Id);
    }

    public static async Task<Profile> CloneAsync(Profile source, string newName, IProgress<DownloadProgress> progress)
    {
        if (DeviceManager.Find(source) != null) throw new InvalidOperationException("Stop the device before copying it.");
        lock (Busy)
        {
            if (!Busy.Add(source.Id)) throw new InvalidOperationException("This profile is already being copied.");
        }

        var copy = new Profile
        {
            Name = newName, OsId = source.OsId, DeviceTypeId = source.DeviceTypeId, RamMb = source.RamMb,
            StorageGb = source.StorageGb, CpuCores = source.CpuCores, Camera = source.Camera, Location = source.Location,
            ManualLatitude = source.ManualLatitude, ManualLongitude = source.ManualLongitude,
            SyncSharedFolder = source.SyncSharedFolder, LaunchDocked = source.LaunchDocked,
            WebAccessEnabled = false, // a copy gets its own port and code when web access is turned on
            PresetApplied = source.PresetApplied, BridgeVersion = source.BridgeVersion,
        };

        var srcDir = AvdWriter.AvdDir(source);
        var dstDir = AvdWriter.AvdDir(copy);
        try
        {
            if (Directory.Exists(srcDir))
            {
                await Task.Run(() => CopyAvd(srcDir, dstDir, progress));
                progress.Report(new DownloadProgress("Finishing copy", 0, 0));
                await RebaseOverlaysAsync(srcDir, dstDir);
            }
            else
            {
                // Never booted: nothing on disk yet, settings are all there is.
                copy.PresetApplied = false;
                copy.BridgeVersion = null;
            }
            ProfileStore.Add(copy);
            Log.Info($"Copied profile {source.Name} ({source.Id}) to {copy.Name} ({copy.Id})");
            return copy;
        }
        catch
        {
            try { if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true); } catch { }
            throw;
        }
        finally
        {
            lock (Busy) Busy.Remove(source.Id);
        }
    }

    private static bool Skip(string relative)
    {
        var name = Path.GetFileName(relative);
        var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
               || top.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
               // Regenerated on every boot and full of absolute paths.
               || name.StartsWith("hardware-qemu.ini", StringComparison.OrdinalIgnoreCase)
               // Quick-boot RAM snapshots are tied to the original; the copy simply does one normal boot.
               || top.Equals("snapshots", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyAvd(string srcDir, string dstDir, IProgress<DownloadProgress> progress)
    {
        var files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories)
            .Select(f => (full: f, rel: Path.GetRelativePath(srcDir, f)))
            .Where(f => !Skip(f.rel))
            .ToList();
        var total = files.Sum(f => new FileInfo(f.full).Length);
        long done = 0;
        var last = DateTime.MinValue;
        var buffer = new byte[4 << 20];

        foreach (var (full, rel) in files)
        {
            var dest = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var sparse = input.Length > 64L << 20 && TryMakeSparse(output.SafeFileHandle);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = buffer.AsSpan(0, read);
                // Disk images are mostly empty: skip zero blocks so the copy doesn't balloon on disk.
                if (sparse && chunk.IndexOfAnyExcept((byte)0) < 0) output.Seek(read, SeekOrigin.Current);
                else output.Write(chunk);
                done += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 300)
                {
                    last = DateTime.UtcNow;
                    progress.Report(new DownloadProgress("Copying apps and data", done, total));
                }
            }
            output.SetLength(input.Length);
        }
        progress.Report(new DownloadProgress("Copying apps and data", total, total));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, uint inSize,
        IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    private static bool TryMakeSparse(SafeFileHandle handle)
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        try { return DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); }
        catch { return false; }
    }

    /// <summary>
    /// qcow2 overlays (the emulator's copy-on-write disks) can store the absolute path of their base image.
    /// Point any that reference the original profile at the copy's own files, so the two are fully independent.
    /// </summary>
    private static async Task RebaseOverlaysAsync(string srcDir, string dstDir)
    {
        var overlays = Directory.GetFiles(dstDir, "*.qcow2", SearchOption.AllDirectories);
        if (overlays.Length == 0) return;

        var qemuImg = Path.Combine(AppPaths.SdkRoot, "emulator", "qemu-img.exe");
        var srcNorm = Path.GetFullPath(srcDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var overlay in overlays)
        {
            if (!File.Exists(qemuImg))
            {
                // Can't inspect it: only safe if we're sure nothing references the original folder.
                var head = new byte[64 * 1024];
                int n;
                await using (var fs = File.OpenRead(overlay)) n = await fs.ReadAsync(head);
                if (System.Text.Encoding.UTF8.GetString(head, 0, n).Contains(Path.GetFileName(srcDir.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Couldn't make an independent copy (qemu-img is missing). Try Settings › Re-download emulator.");
                continue;
            }

            var info = await ProcessRunner.RunAsync(qemuImg, new[] { "info", "--output=json", "-U", overlay }, TimeSpan.FromSeconds(60));
            if (!info.Ok) throw new InvalidOperationException("Couldn't inspect a disk image of the copy: " + info.Combined);
            string? backing = null, format = null;
            using (var doc = JsonDocument.Parse(info.Output))
            {
                if (doc.RootElement.TryGetProperty("full-backing-filename", out var fb)) backing = fb.GetString();
                else if (doc.RootElement.TryGetProperty("backing-filename", out var b)) backing = b.GetString();
                if (doc.RootElement.TryGetProperty("backing-filename-format", out var f)) format = f.GetString();
            }
            if (string.IsNullOrEmpty(backing)) continue;

            var resolved = Path.IsPathRooted(backing) ? Path.GetFullPath(backing) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(overlay)!, backing));
            if (!resolved.StartsWith(srcNorm, StringComparison.OrdinalIgnoreCase)) continue; // shared system image: fine

            var newBacking = Path.Combine(dstDir, resolved[srcNorm.Length..]);
            var args = new List<string> { "rebase", "-u", "-b", newBacking };
            if (!string.IsNullOrEmpty(format)) { args.Add("-F"); args.Add(format); }
            args.Add(overlay);
            var r = await ProcessRunner.RunAsync(qemuImg, args, TimeSpan.FromSeconds(60));
            if (!r.Ok) throw new InvalidOperationException("Couldn't finish the copy: " + r.Combined);
            Log.Info($"Rebased {overlay} -> {newBacking}");
        }
    }
}
