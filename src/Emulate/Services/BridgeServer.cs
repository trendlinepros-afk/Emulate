using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Emulate.Models;

namespace Emulate.Services;

/// <summary>
/// Tiny HTTP server on 127.0.0.1 that the "PC Bridge" Android app (installed on every device) talks to.
/// Inside the emulator the PC is reachable at 10.0.2.2. It lets the device:
///  - open a real Windows File Explorer dialog and receive the picked files (/pick, /file)
///  - browse the PC's drives and folders from Android's file picker (/roots, /list, /stat, /get)
///  - save files back to the PC (/upload) into Downloads\Emulate
/// Every request must carry the per-install secret token, which is only ever handed to devices over adb.
/// </summary>
public static class BridgeServer
{
    private static TcpListener? _listener;
    private static readonly ConcurrentDictionary<string, string> Picked = new();
    private static readonly SemaphoreSlim PickLock = new(1, 1);

    public static int Port { get; private set; }
    public static bool Running => _listener != null;

    public static void Start()
    {
        var port = AppSettings.Current.BridgePort;
        for (var attempt = 0; attempt < 20; attempt++, port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listener = l;
                Port = port;
                Log.Info($"PC bridge listening on 127.0.0.1:{port}");
                _ = Task.Run(AcceptLoop);
                return;
            }
            catch (SocketException)
            {
                // Port busy, try the next one.
            }
        }
        Log.Error("PC bridge could not find a free port");
    }

    private static async Task AcceptLoop()
    {
        while (_listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (Exception ex)
            {
                Log.Error("Bridge accept failed", ex);
                await Task.Delay(500);
            }
        }
    }

    private sealed class Request
    {
        public string Method = "GET";
        public string Path = "/";
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public long ContentLength;
    }

    private static async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            var req = await ReadHeadAsync(stream);
            if (req == null) return;

            var token = req.Headers.GetValueOrDefault("X-Emulate-Token") ?? req.Query.GetValueOrDefault("token");
            if (token != AppSettings.Current.BridgeToken)
            {
                await SendTextAsync(stream, 403, "Forbidden");
                return;
            }

            switch (req.Path)
            {
                case "/ping":
                    await SendJsonAsync(stream, new { ok = true, app = "Emulate", version = UpdateService.CurrentVersion.ToString() });
                    break;
                case "/pick":
                    await HandlePickAsync(stream, req);
                    break;
                case "/file":
                    if (Picked.TryGetValue(req.Query.GetValueOrDefault("id") ?? "", out var pickedPath))
                        await SendFileAsync(stream, pickedPath);
                    else
                        await SendTextAsync(stream, 404, "Unknown file id");
                    break;
                case "/roots":
                    await SendJsonAsync(stream, Roots());
                    break;
                case "/list":
                    await HandleListAsync(stream, req.Query.GetValueOrDefault("path") ?? "");
                    break;
                case "/stat":
                    await HandleStatAsync(stream, req.Query.GetValueOrDefault("path") ?? "");
                    break;
                case "/get":
                    var path = req.Query.GetValueOrDefault("path") ?? "";
                    if (File.Exists(path)) await SendFileAsync(stream, path);
                    else await SendTextAsync(stream, 404, "Not found");
                    break;
                case "/upload":
                    await HandleUploadAsync(stream, req);
                    break;
                default:
                    await SendTextAsync(stream, 404, "Not found");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bridge request failed", ex);
            try { await SendTextAsync(stream, 500, ex.Message); } catch { }
        }
    }

    private static async Task<Request?> ReadHeadAsync(NetworkStream stream)
    {
        // Read byte-by-byte until the blank line so the body stays in the stream for uploads.
        var head = new List<byte>(1024);
        var one = new byte[1];
        while (head.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
        }
        var lines = Encoding.UTF8.GetString(head.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var req = new Request { Method = first[0] };
        var target = first[1];
        var q = target.IndexOf('?');
        req.Path = q >= 0 ? target[..q] : target;
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                var v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                req.Query[k] = v;
            }
        }
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c > 0) req.Headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        long.TryParse(req.Headers.GetValueOrDefault("Content-Length"), out req.ContentLength);
        return req;
    }

    // ---------- Windows file picker ----------

    private static async Task HandlePickAsync(NetworkStream stream, Request req)
    {
        var mime = req.Query.GetValueOrDefault("mime") ?? "*/*";
        var multi = req.Query.GetValueOrDefault("multi") == "1";

        // One dialog at a time.
        await PickLock.WaitAsync();
        string[] files;
        try
        {
            files = await Application.Current.Dispatcher.InvokeAsync(() => ShowPicker(mime, multi)).Task;
        }
        finally
        {
            PickLock.Release();
        }

        var result = files.Select(f =>
        {
            var id = Guid.NewGuid().ToString("N");
            Picked[id] = f;
            var info = new FileInfo(f);
            return new { id, name = info.Name, size = info.Length, mime = Mime.FromPath(f) };
        }).ToArray();
        if (result.Length > 0) Log.Say($"Sent {result.Length} file(s) from the PC to the device.");
        await SendJsonAsync(stream, result);
    }

    private static string[] ShowPicker(string mime, bool multi)
    {
        // A hidden top-most owner window forces the dialog in front of the emulator window.
        var owner = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true,
            ShowActivated = true, AllowsTransparency = true, Opacity = 0, Left = SystemParameters.WorkArea.Width / 2,
            Top = SystemParameters.WorkArea.Height / 3,
        };
        owner.Show();
        owner.Activate();
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose file(s) to send to your Android device",
                Multiselect = multi,
                Filter = Mime.DialogFilter(mime),
                InitialDirectory = mime.StartsWith("image") || mime.StartsWith("video")
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            return dlg.ShowDialog(owner) == true ? dlg.FileNames : Array.Empty<string>();
        }
        finally
        {
            owner.Close();
        }
    }

    // ---------- PC browsing (Android DocumentsProvider) ----------

    private static object[] Roots()
    {
        var list = new List<object>();
        void AddFolder(Environment.SpecialFolder f, string label)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(new { name = label, path = p, dir = true, size = 0L, modified = 0L, mime = "" });
        }
        AddFolder(Environment.SpecialFolder.Desktop, "Desktop");
        AddFolder(Environment.SpecialFolder.MyDocuments, "Documents");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) list.Add(new { name = "Downloads", path = downloads, dir = true, size = 0L, modified = 0L, mime = "" });
        AddFolder(Environment.SpecialFolder.MyPictures, "Pictures");
        AddFolder(Environment.SpecialFolder.MyVideos, "Videos");
        AddFolder(Environment.SpecialFolder.MyMusic, "Music");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"Drive {d.Name.TrimEnd('\\')}" : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
            list.Add(new { name = label, path = d.RootDirectory.FullName, dir = true, size = 0L, modified = 0L, mime = "" });
        }
        return list.ToArray();
    }

    private static object Entry(FileSystemInfo i) => new
    {
        name = i.Name,
        path = i.FullName,
        dir = i is DirectoryInfo,
        size = i is FileInfo f ? f.Length : 0L,
        modified = new DateTimeOffset(i.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        mime = i is FileInfo ? Mime.FromPath(i.FullName) : "",
    };

    private static async Task HandleListAsync(NetworkStream stream, string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            await SendTextAsync(stream, 404, "Not found");
            return;
        }
        var entries = new List<object>();
        try
        {
            foreach (var i in dir.EnumerateFileSystemInfos())
            {
                if ((i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                entries.Add(Entry(i));
                if (entries.Count >= 5000) break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Return what we could read.
        }
        await SendJsonAsync(stream, entries);
    }

    private static async Task HandleStatAsync(NetworkStream stream, string path)
    {
        FileSystemInfo i = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!i.Exists)
        {
            await SendTextAsync(stream, 404, "Not found");
            return;
        }
        await SendJsonAsync(stream, Entry(i));
    }

    // ---------- Device -> PC ----------

    private static async Task HandleUploadAsync(NetworkStream stream, Request req)
    {
        var name = Path.GetFileName(req.Query.GetValueOrDefault("name") ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = $"file-{DateTime.Now:yyyyMMdd-HHmmss}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        Directory.CreateDirectory(AppPaths.FromDeviceFolder);
        var dest = Path.Combine(AppPaths.FromDeviceFolder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(AppPaths.FromDeviceFolder, $"{stem} ({n}){ext}");

        await using (var fs = File.Create(dest))
        {
            var remaining = req.ContentLength;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        Log.Say($"Saved '{Path.GetFileName(dest)}' from the device to {AppPaths.FromDeviceFolder}");
        await SendJsonAsync(stream, new { ok = true, path = dest });
    }

    // ---------- HTTP helpers ----------

    private static async Task SendHeadAsync(Stream s, int status, string contentType, long length, string? extra = null)
    {
        var reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nConnection: close\r\n{extra}\r\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(head));
    }

    private static async Task SendTextAsync(Stream s, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        await SendHeadAsync(s, status, "text/plain; charset=utf-8", body.Length);
        await s.WriteAsync(body);
    }

    private static async Task SendJsonAsync(Stream s, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        await SendHeadAsync(s, 200, "application/json", body.Length);
        await s.WriteAsync(body);
    }

    private static async Task SendFileAsync(Stream s, string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await SendHeadAsync(s, 200, Mime.FromPath(path), fs.Length);
        await fs.CopyToAsync(s);
    }
}

public static class Mime
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".mkv"] = "video/x-matroska", [".webm"] = "video/webm",
        [".3gp"] = "video/3gpp", [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg", [".opus"] = "audio/opus", [".flac"] = "audio/flac",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".csv"] = "text/csv", [".html"] = "text/html",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip", [".apk"] = "application/vnd.android.package-archive",
        [".json"] = "application/json", [".vcf"] = "text/x-vcard",
    };

    public static string FromPath(string path) => Map.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    public static bool IsMedia(string path)
    {
        var m = FromPath(path);
        return m.StartsWith("image/") || m.StartsWith("video/");
    }

    public static string DialogFilter(string mime)
    {
        string Exts(string prefix) => string.Join(";", Map.Where(kv => kv.Value.StartsWith(prefix)).Select(kv => "*" + kv.Key));
        var all = "All files (*.*)|*.*";
        if (mime.StartsWith("image/") && mime != "image/*" && !mime.EndsWith("*")) mime = "image/*";
        return mime switch
        {
            "image/*" => $"Images|{Exts("image/")}|{all}",
            "video/*" => $"Videos|{Exts("video/")}|{all}",
            "audio/*" => $"Audio|{Exts("audio/")}|{all}",
            _ when mime.Contains("image") && mime.Contains("video") => $"Photos & videos|{Exts("image/")};{Exts("video/")}|{all}",
            _ => all,
        };
    }
}
