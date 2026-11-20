using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class ShortcutRecord
{
    public string Id { get; set; }
    public string PathOnDesktop { get; set; }
    public string OriginalHash { get; set; }
    public string AppliedHash { get; set; }
    public string LauncherPath { get; set; }
    public bool Undone { get; set; }
    public IntroSettings Settings { get; set; }

    public override string ToString()
    {
        return Path.GetFileNameWithoutExtension(PathOnDesktop)
            + (OriginalHash == null ? "　·　删除新建入口" : "　·　恢复原快捷方式");
    }
}

internal sealed class ShortcutStore
{
    private readonly string root;
    private readonly string desktop;
    private readonly string launcher;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public ShortcutStore(string root, string desktop, string launcher)
    {
        this.root = Path.GetFullPath(root);
        this.desktop = Path.GetFullPath(desktop).TrimEnd(Path.DirectorySeparatorChar);
        this.launcher = Path.GetFullPath(launcher);
    }

    public bool IsDesktopLink(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)
            && SamePath(Path.GetDirectoryName(Path.GetFullPath(path)), desktop);
    }

    public IntroSettings ForPlayback(string targetId)
    {
        var record = Read(targetId);
        if (record == null || !IsManaged(record, HashFile(record.PathOnDesktop))) return null;
        return WithRecordedTarget(record, record.Settings);
    }

    public IntroSettings ForPreview(IntroSettings settings)
    {
        var record = FindActive(settings.Target);
        if (record != null) return WithRecordedTarget(record, settings);
        if (IsIntroShortcut(settings.Target))
            throw new InvalidOperationException("请选择软件原来的快捷方式或程序文件。");
        return settings;
    }

    public ShortcutRecord Apply(IntroSettings settings)
    {
        if (!settings.HasTarget()) throw new InvalidOperationException("目标软件已移动，请重新选择。");
        if (SamePath(settings.Target, launcher)) throw new InvalidOperationException("请选择要启动的软件。");
        using (LockChanges())
        {
            var active = FindActive(settings.Target);
            if (active != null)
            {
                active.Settings = WithRecordedTarget(active, settings);
                WriteRecord(active);
                return active;
            }
            if (IsIntroShortcut(settings.Target))
                throw new InvalidOperationException("这个快捷方式已经指向片头。请选软件的原入口。");

            bool replace = IsDesktopLink(settings.Target);
            string path = replace ? Path.GetFullPath(settings.Target) : AvailableName(settings.AppName);
            string id = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            string originalHash = null;
            if (replace)
            {
                byte[] original = File.ReadAllBytes(path);
                originalHash = Hash(original);
                File.WriteAllBytes(Path.Combine(folder, "原快捷方式.lnk"), original);
                File.WriteAllBytes(Path.Combine(folder, "启动入口.lnk"), original);
            }
            string staged = Path.Combine(folder, "staged.lnk");
            dynamic shell = CreateShell();
            dynamic link = shell.CreateShortcut(staged);
            link.TargetPath = launcher;
            link.Arguments = "--shortcut " + id;
            link.WorkingDirectory = Path.GetDirectoryName(launcher);
            link.Description = "播放片头并打开 " + Path.GetFileNameWithoutExtension(path);
            link.IconLocation = OriginalIcon(shell, settings.Target, folder);
            link.Save();
            byte[] replacement = File.ReadAllBytes(staged);
            File.Delete(staged);
            var record = new ShortcutRecord
            {
                Id = id, PathOnDesktop = path, Settings = settings, LauncherPath = launcher,
                OriginalHash = originalHash, AppliedHash = Hash(replacement)
            };
            WriteRecord(record);
            PutDesktopLink(path, replacement, originalHash);
            return record;
        }
    }

    public List<ShortcutRecord> ListForUndo()
    {
        var records = new List<ShortcutRecord>();
        if (!Directory.Exists(root)) return records;
        foreach (string directory in Directory.GetDirectories(root))
        {
            var record = Read(Path.GetFileName(directory));
            if (record == null) continue;
            try
            {
                string current = HashFile(record.PathOnDesktop);
                if (IsManaged(record, current) || (current == null && record.OriginalHash != null))
                    records.Add(record);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        records.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString()));
        return records;
    }

    public void Undo(string id)
    {
        using (LockChanges())
        {
            var record = Read(id);
            if (record == null) throw new InvalidOperationException("找不到这条备份记录。");
            string current = HashFile(record.PathOnDesktop);
            if (current != null && !IsManaged(record, current))
                throw new InvalidOperationException("这个快捷方式已被改动。请保留当前文件，避免覆盖。");
            if (record.OriginalHash != null)
            {
                string backup = OriginalPath(record);
                if (HashFile(backup) != record.OriginalHash)
                    throw new InvalidOperationException("原快捷方式备份无法读取。");
                PutDesktopLink(record.PathOnDesktop, File.ReadAllBytes(backup), current);
            }
            else if (current != null)
            {
                File.Delete(record.PathOnDesktop);
            }
            record.Undone = true;
            WriteRecord(record);
        }
    }

    private ShortcutRecord FindActive(string path)
    {
        if (!IsDesktopLink(path) || !Directory.Exists(root)) return null;
        string current = HashFile(path);
        if (current == null) return null;
        foreach (string folder in Directory.GetDirectories(root))
        {
            var record = Read(Path.GetFileName(folder));
            if (record != null && SamePath(path, record.PathOnDesktop) && IsManaged(record, current))
                return record;
        }
        return null;
    }

    private static bool IsManaged(ShortcutRecord record, string current)
    {
        if (current == null) return false;
        if (current == record.AppliedHash) return true;
        try
        {
            dynamic shell = CreateShell();
            dynamic link = shell.CreateShortcut(record.PathOnDesktop);
            return SamePath((string)link.TargetPath, record.LauncherPath)
                && string.Equals((string)link.Arguments, "--shortcut " + record.Id, StringComparison.Ordinal);
        }
        catch (COMException) { return false; }
    }

    private IntroSettings WithRecordedTarget(ShortcutRecord record, IntroSettings settings)
    {
        return new IntroSettings
        {
            Target = record.OriginalHash == null ? record.Settings.Target : Path.Combine(root, record.Id, "启动入口.lnk"),
            ApplicationLabel = Path.GetFileNameWithoutExtension(record.PathOnDesktop),
            Title = settings.Title, Greeting = settings.Greeting, Picture = settings.Picture,
            WindowTitle = settings.WindowTitle, Maximize = settings.Maximize
        };
    }

    private bool IsIntroShortcut(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        dynamic shell = CreateShell();
        dynamic link = shell.CreateShortcut(path);
        string target = link.TargetPath;
        string arguments = link.Arguments;
        if (SamePath(target, launcher)) return true;
        string name = Path.GetFileName(target);
        bool launcherName = string.Equals(name, Branding.LauncherFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "念安通用片头.exe", StringComparison.OrdinalIgnoreCase);
        return launcherName && arguments != null && (arguments == "--play"
            || arguments.StartsWith("--shortcut ", StringComparison.Ordinal));
    }

    private string AvailableName(string name)
    {
        string path = Path.Combine(desktop, name + "（片头）.lnk");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(desktop, name + "（片头 " + suffix + "）.lnk");
        return path;
    }

    private ShortcutRecord Read(string id)
    {
        Guid key;
        if (!Guid.TryParseExact(id, "N", out key)) return null;
        string path = RecordPath(id);
        try
        {
            if (!File.Exists(path)) return null;
            var record = json.Deserialize<ShortcutRecord>(File.ReadAllText(path));
            return record != null && record.Id == id && !record.Undone && record.Settings != null
                && IsDesktopLink(record.PathOnDesktop) ? record : null;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string RecordPath(string id) { return Path.Combine(root, id, "record.json"); }
    private string OriginalPath(ShortcutRecord record) { return Path.Combine(root, record.Id, "原快捷方式.lnk"); }

    private void WriteRecord(ShortcutRecord record)
    {
        string path = RecordPath(record.Id);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, json.Serialize(record), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream LockChanges()
    {
        Directory.CreateDirectory(root);
        return new FileStream(Path.Combine(root, "changes.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void PutDesktopLink(string path, byte[] contents, string expectedHash)
    {
        if (!IsDesktopLink(path)) throw new InvalidOperationException("请选择当前用户桌面上的快捷方式。");
        string staging = Path.Combine(desktop, ".nianan-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(staging, contents);
            if (HashFile(path) != expectedHash)
                throw new InvalidOperationException("快捷方式正在被改动，请重新选择后再试。");
            if (expectedHash == null) File.Move(staging, path);
            else File.Replace(staging, path, null);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static string HashFile(string path)
    {
        return File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
    }

    private static string Hash(byte[] bytes)
    {
        using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
    }

    private static dynamic CreateShell()
    {
        return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
    }

    private static string OriginalIcon(dynamic shell, string path, string folder)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return path + ",0";
        dynamic source = shell.CreateShortcut(path);
        string icon = source.IconLocation;
        if (!string.IsNullOrWhiteSpace(icon) && !icon.StartsWith(",", StringComparison.Ordinal)) return icon;
        string target = source.TargetPath;
        if (File.Exists(target)) return target + ",0";
        ShellFileInfo info;
        SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x100);
        if (info.Icon == IntPtr.Zero)
            throw new InvalidOperationException("无法读取原图标。请选择软件的普通桌面快捷方式。");
        try
        {
            string saved = Path.Combine(folder, "icon.ico");
            using (var stream = File.Create(saved))
            using (var preview = Icon.FromHandle(info.Icon)) preview.Save(stream);
            return saved + ",0";
        }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
