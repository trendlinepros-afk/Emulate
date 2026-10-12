using System.Diagnostics;

namespace Emulate.Services;

/// <summary>Thin wrapper around the platform-tools adb.exe.</summary>
public static class Adb
{
    private static readonly SemaphoreSlim ServerLock = new(1, 1);
    private static bool _serverStarted;

    /// <summary>
    /// Starts the adb server without redirecting its output (the daemon would otherwise inherit and hold our pipes).
    /// </summary>
    public static async Task EnsureServerAsync()
    {
        if (_serverStarted) return;
        await ServerLock.WaitAsync();
        try
        {
            if (_serverStarted || !File.Exists(AppPaths.Adb)) return;
            var psi = new ProcessStartInfo(AppPaths.Adb) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("start-server");
            AppPaths.ApplyAndroidEnvironment(psi.Environment);
            using var p = Process.Start(psi)!;
            await Task.Run(() => p.WaitForExit(30000));
            _serverStarted = true;
        }
        catch (Exception ex)
        {
            Log.Error("adb start-server failed", ex);
        }
        finally
        {
            ServerLock.Release();
        }
    }

    public static async Task<ProcessResult> RunAsync(params string[] args) => await RunAsync(TimeSpan.FromMinutes(2), args);

    public static async Task<ProcessResult> RunAsync(TimeSpan timeout, params string[] args)
    {
        await EnsureServerAsync();
        var r = await ProcessRunner.RunAsync(AppPaths.Adb, args, timeout);
        if (!r.Ok) Log.Info($"adb {string.Join(' ', args)} -> {r.ExitCode}: {r.Combined}");
        return r;
    }

    public static Task<ProcessResult> ShellAsync(string serial, string command, TimeSpan? timeout = null) =>
        RunAsync(timeout ?? TimeSpan.FromSeconds(60), "-s", serial, "shell", command);

    public static Task<ProcessResult> EmuAsync(string serial, params string[] command) =>
        RunAsync(TimeSpan.FromSeconds(20), new[] { "-s", serial, "emu" }.Concat(command).ToArray());

    /// <summary>Serials of all emulators adb currently sees as online.</summary>
    public static async Task<List<string>> ListEmulatorsAsync()
    {
        var r = await RunAsync(TimeSpan.FromSeconds(15), "devices");
        return r.Output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("emulator-") && l.EndsWith("device"))
            .Select(l => l.Split('\t', ' ')[0])
            .ToList();
    }

    /// <summary>Asks a running emulator for its AVD name ("emu avd name").</summary>
    public static async Task<string?> GetAvdNameAsync(string serial)
    {
        var r = await EmuAsync(serial, "avd", "name");
        if (!r.Ok) return null;
        return r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
    }

    /// <summary>Quotes a string for the Android (mksh) shell.</summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
