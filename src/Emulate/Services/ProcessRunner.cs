using System.Diagnostics;
using System.Text;

namespace Emulate.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => (Output + "\n" + Error).Trim();
}

public static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        AppPaths.ApplyAndroidEnvironment(psi.Environment);
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(exe, args);
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // WaitForExit(int) is bounded even when a spawned daemon (adb server) keeps our pipes open,
        // unlike WaitForExitAsync which waits for the pipes to close.
        var ms = (int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds;
        await Task.Run(() => p.WaitForExit(ms), ct);
        if (!p.HasExited)
        {
            try { p.Kill(true); } catch { }
            return new ProcessResult(-1, stdout.ToString(), "Timed out: " + stderr);
        }
        string o, e;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e = stderr.ToString();
        return new ProcessResult(p.ExitCode, o, e);
    }

    /// <summary>Runs a process and captures raw stdout bytes (for screenshots).</summary>
    public static async Task<byte[]> RunBinaryAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = CreateStartInfo(exe, args);
        psi.StandardOutputEncoding = null;
        using var p = new Process { StartInfo = psi };
        p.Start();
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeout);
        await p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return ms.ToArray();
    }
}
