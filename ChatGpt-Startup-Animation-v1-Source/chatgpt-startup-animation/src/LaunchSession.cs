using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class LaunchSession
{
    public static void Play(IntroSettings settings)
    {
        string browser = FindBrowser();
        if (browser == null)
        {
            MessageBox.Show("全屏片头需要 Microsoft Edge 或 Google Chrome。", Branding.ProductName);
            return;
        }
        using (var preview = new LoopbackPreview(settings))
        using (var player = StartPreview(browser, preview.Url))
        {
            IntPtr cover = IntPtr.Zero;
            try
            {
                var startup = Stopwatch.StartNew();
                while (startup.ElapsedMilliseconds < 6000 && !player.HasExited)
                {
                    player.Refresh();
                    cover = player.MainWindowHandle;
                    if (cover != IntPtr.Zero) break;
                    Thread.Sleep(35);
                }
                if (cover == IntPtr.Zero)
                {
                    StartTarget(settings.Target);
                    return;
                }
                TargetWindows.Pin(cover);
                var target = new TargetWindows(settings, StartTargetOrReport(settings.Target, preview));
                while (!player.HasExited && !preview.Dismissed)
                {
                    var playing = Stopwatch.StartNew();
                    while (!preview.HandoffRequested && !preview.Dismissed && !player.HasExited
                        && playing.ElapsedMilliseconds < 18000)
                    {
                        if (TargetWindows.IsForeground(target.Find())) TargetWindows.Focus(cover);
                        Thread.Sleep(180);
                    }
                    if (preview.Dismissed || player.HasExited) return;
                    if (preview.Error == null && WaitForTarget(settings, target, preview, player, cover)) return;
                    if (preview.Error == null)
                        preview.Error = "还没找到软件的主窗口。若选的是启动器，可在设置里填写窗口标题关键词。";
                    while (!preview.RetryRequested && !preview.Dismissed && !player.HasExited) Thread.Sleep(100);
                    if (preview.Dismissed || player.HasExited) return;
                    preview.BeginRetry();
                    target = new TargetWindows(settings, StartTargetOrReport(settings.Target, preview));
                }
            }
            finally
            {
                if (!player.HasExited && player.CloseMainWindow()) player.WaitForExit(1100);
                if (!player.HasExited) player.Kill();
            }
        }
    }

    private static bool WaitForTarget(IntroSettings settings, TargetWindows target,
        LoopbackPreview preview, Process player, IntPtr cover)
    {
        var waiting = Stopwatch.StartNew();
        IntPtr observed = IntPtr.Zero;
        long stableSince = 0;
        long sentSizeAt = -1000;
        while (waiting.ElapsedMilliseconds < 32000 && !preview.Dismissed && !player.HasExited)
        {
            IntPtr current = target.Find();
            long now = waiting.ElapsedMilliseconds;
            if (current != observed)
            {
                observed = current;
                stableSince = now;
                sentSizeAt = -1000;
            }
            if (current != IntPtr.Zero)
            {
                bool maximize = settings.Maximize && TargetWindows.CanMaximize(current);
                bool needsSize = TargetWindows.IsIconic(current) || (maximize && !TargetWindows.IsZoomed(current));
                if (needsSize && now - sentSizeAt >= 650)
                {
                    TargetWindows.ShowWindowAsync(current, maximize ? 3 : 9); // SW_MAXIMIZE or SW_RESTORE
                    sentSizeAt = stableSince = now;
                }
                if (!needsSize && now - stableSince >= 425 && TargetWindows.MainWindowReady(current)
                    && TargetWindows.Reveal(cover, current)) return true;
            }
            Thread.Sleep(90);
        }
        return false;
    }

    private static int StartTarget(string file)
    {
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = file, WorkingDirectory = Path.GetDirectoryName(file), UseShellExecute = true
        })) return process == null ? 0 : process.Id;
    }

    private static int StartTargetOrReport(string file, LoopbackPreview preview)
    {
        try { return StartTarget(file); }
        catch (Exception)
        {
            preview.Error = "Windows 无法打开所选软件。请确认原快捷方式仍然可用。";
            return 0;
        }
    }

    private static string FindBrowser()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (string name in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
        {
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                string file = Path.Combine(folder, name);
                if (File.Exists(file)) return file;
            }
        }
        return null;
    }

    private static Process StartPreview(string browser, string url)
    {
        bool edge = string.Equals(Path.GetFileName(browser), "msedge.exe", StringComparison.OrdinalIgnoreCase);
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Branding.ProfileFolder, "preview");
        string arguments = "--kiosk \"" + url + "\" --user-data-dir=\"" + profile
            + "\" --no-first-run --disable-background-mode";
        if (edge) arguments += " --edge-kiosk-type=fullscreen";
        return Process.Start(new ProcessStartInfo { FileName = browser, Arguments = arguments, UseShellExecute = false });
    }
}

internal sealed class LoopbackPreview : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly string prefix = "/" + Guid.NewGuid().ToString("N") + "/";
    private readonly string host;
    private readonly Dictionary<string, Asset> assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
    private readonly Thread acceptThread;
    private volatile bool disposed;
    public volatile bool HandoffRequested;
    public volatile bool RetryRequested;
    public volatile bool Dismissed;
    public volatile string Error;
    public string Url { get { return "http://" + host + prefix; } }

    public LoopbackPreview(IntroSettings settings)
    {
        Add("index.html", "index.html", "text/html; charset=utf-8");
        Add("style.css", "style.css", "text/css; charset=utf-8");
        Add("intro.js", "intro.js", "text/javascript; charset=utf-8");
        Add("portable.js", "portable.js", "text/javascript; charset=utf-8");
        byte[] picture;
        string pictureType;
        if (settings.ReadPicture(out picture, out pictureType) && picture != null)
            assets.Add("assets/pearl.webp", new Asset(picture, pictureType));
        else
            Add("assets/pearl.webp", "pearl.webp", "image/webp");

        assets.Add("settings", Json(new { title = settings.Title, greeting = settings.Greeting, app = settings.AppName }));
        listener.Start(16);
        host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptThread = new Thread(Accept) { IsBackground = true };
        acceptThread.Start();
    }

    public void BeginRetry()
    {
        RetryRequested = false;
        Error = null;
    }

    private void Add(string key, string file, string contentType)
    {
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("StartupAnimation." + file))
        using (var data = new MemoryStream())
        {
            resource.CopyTo(data);
            assets.Add(key, new Asset(data.ToArray(), contentType));
        }
    }

    private void Accept()
    {
        while (!disposed)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            ThreadPool.QueueUserWorkItem(unused => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 2500;
                using (var stream = client.GetStream())
                {
                    string request = ReadHeader(stream);
                    if (request == null) return;
                    string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] parts = lines[0].Split(' ');
                    if (parts.Length != 3 || !parts[1].StartsWith(prefix, StringComparison.Ordinal)
                        || !HasHeader(lines, "Host", host) || HasHeader(lines, "Sec-Fetch-Site", "cross-site"))
                    {
                        Write(stream, "403 Forbidden", null);
                        return;
                    }
                    string route = parts[1].Substring(prefix.Length);
                    if (route.Length == 0) route = "index.html";
                    Asset asset;
                    if (parts[0] == "GET" && assets.TryGetValue(route, out asset)) Write(stream, "200 OK", asset);
                    else if (parts[0] == "GET" && route == "status") Write(stream, "200 OK", Json(new { error = Error }));
                    else if (parts[0] == "POST" && HasHeader(lines, "Origin", "http://" + host))
                    {
                        if (route == "handoff") HandoffRequested = true;
                        else if (route == "retry") RetryRequested = true;
                        else if (route == "dismiss") Dismissed = true;
                        else { Write(stream, "404 Not Found", null); return; }
                        Write(stream, "204 No Content", null);
                    }
                    else Write(stream, "404 Not Found", null);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private static string ReadHeader(Stream stream)
    {
        var bytes = new byte[8192];
        for (int count = 0; count < bytes.Length;)
        {
            int got = stream.Read(bytes, count, bytes.Length - count);
            if (got == 0) break;
            int end = count + got;
            for (int i = Math.Max(3, count); i < end; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, i + 1);
            }
            count = end;
        }
        return null;
    }

    private static bool HasHeader(string[] lines, string key, string value)
    {
        string marker = key + ":";
        foreach (string line in lines)
            if (line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                && string.Equals(line.Substring(marker.Length).Trim(), value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Asset Json(object value)
    {
        return new Asset(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)), "application/json; charset=utf-8");
    }

    private static void Write(Stream stream, string status, Asset asset)
    {
        string header = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-store\r\n"
            + "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n"
            + "Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n"
            + "Content-Length: " + (asset == null ? 0 : asset.Data.Length) + "\r\n";
        if (asset != null) header += "Content-Type: " + asset.Type + "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(header + "\r\n");
        stream.Write(bytes, 0, bytes.Length);
        if (asset != null) stream.Write(asset.Data, 0, asset.Data.Length);
    }

    public void Dispose()
    {
        disposed = true;
        listener.Stop();
        acceptThread.Join(500);
    }

    private sealed class Asset
    {
        public readonly byte[] Data;
        public readonly string Type;
        public Asset(byte[] data, string contentType) { Data = data; Type = contentType; }
    }
}
