using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal sealed class TargetWindows
{
    private readonly string executable;
    private readonly string keyword;
    private readonly int launchedPid;

    public TargetWindows(IntroSettings settings, int processId)
    {
        launchedPid = processId;
        keyword = settings.WindowTitle ?? "";
        executable = ResolveExecutable(settings.Target);
    }

    private static string ResolveExecutable(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath;
            return string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        catch (Exception) { return null; }
    }

    public IntPtr Find()
    {
        IntPtr best = IntPtr.Zero;
        long bestScore = 0;
        EnumWindows((window, unused) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true; // GW_OWNER
            int style = GetWindowLong(window, -16);
            if ((style & (0x00080000 | 0x00040000)) == 0 && keyword.Length == 0) return true;
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            string image = ProcessImage(pid);
            string name = Path.GetFileName(image);
            bool systemHost = string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rundll32.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            bool exactPath = !systemHost && executable != null
                && string.Equals(executable, image, StringComparison.OrdinalIgnoreCase);
            bool exactPid = !systemHost && launchedPid > 0 && launchedPid == pid;
            if (!exactPath && !exactPid && keyword.Length == 0) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.Length == 0 || (keyword.Length > 0
                && title.ToString().IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)) return true;
            int cloaked;
            if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Rect bounds;
            if (!GetWindowRect(window, out bounds)) return true;
            bool iconic = IsIconic(window);
            if (!iconic && (bounds.Right - bounds.Left < 240 || bounds.Bottom - bounds.Top < 145)) return true;
            long score = iconic ? 1 : (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);
            if (exactPath) score += 100000000L;
            if (exactPid) score += 50000000L;
            if (score > bestScore) { best = window; bestScore = score; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static string ProcessImage(uint processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return "";
        try
        {
            var name = new StringBuilder(2048);
            int length = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString() : "";
        }
        finally { CloseHandle(process); }
    }

    public static bool MainWindowReady(IntPtr window)
    {
        IntPtr answer;
        return window != IntPtr.Zero && IsWindowVisible(window) && !IsIconic(window)
            && SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 120, out answer) != IntPtr.Zero;
    }

    public static bool CanMaximize(IntPtr window)
    {
        return (GetWindowLong(window, -16) & 0x00010000) != 0; // WS_MAXIMIZEBOX
    }

    public static void Pin(IntPtr window)
    {
        if (window != IntPtr.Zero) SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    }

    public static bool Focus(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        if (IsForeground(window) || SetForegroundWindow(window)) return true;
        uint ignored;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
        uint thread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == thread
            || !AttachThreadInput(thread, foregroundThread, true)) return false;
        try
        {
            BringWindowToTop(window);
            return SetForegroundWindow(window) || IsForeground(window);
        }
        finally { AttachThreadInput(thread, foregroundThread, false); }
    }

    public static bool IsForeground(IntPtr window)
    {
        return window != IntPtr.Zero && GetForegroundWindow() == window;
    }

    public static bool Reveal(IntPtr preview, IntPtr target)
    {
        Focus(target);
        Thread.Sleep(85);
        if (!IsForeground(target) || !MainWindowReady(target)) return false;
        Rect cover, bounds;
        if (preview == IntPtr.Zero || !GetWindowRect(preview, out cover) || !GetWindowRect(target, out bounds)
            || bounds.Left > cover.Left || bounds.Top > cover.Top || bounds.Right < cover.Right
            || bounds.Bottom < cover.Bottom) return true;

        int style = GetWindowLong(preview, -20);
        SetWindowLong(preview, -20, style | 0x00080000);
        var timer = Stopwatch.StartNew();
        while (IsForeground(target))
        {
            double time = Math.Min(timer.Elapsed.TotalMilliseconds / 430, 1);
            byte opacity = (byte)Math.Round(255 * (1 - time * time * (3 - 2 * time)));
            if (!SetLayeredWindowAttributes(preview, 0, opacity, 2)) break;
            if (time == 1) return true;
            Thread.Sleep(15);
        }
        SetLayeredWindowAttributes(preview, 0, 255, 2);
        SetWindowLong(preview, -20, style);
        return IsForeground(target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint relationship);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int field);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int field, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint other, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte opacity, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr answer);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
