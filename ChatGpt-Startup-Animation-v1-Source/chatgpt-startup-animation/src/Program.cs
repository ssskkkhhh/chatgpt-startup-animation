using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class IntroSettings
{
    public string Target { get; set; }
    public string WindowTitle { get; set; }
    public string Title { get; set; }
    public string Greeting { get; set; }
    public string Picture { get; set; }
    public bool Maximize { get; set; }

    [ScriptIgnore]
    public string ApplicationLabel { get; set; }

    public IntroSettings()
    {
        Target = WindowTitle = Picture = "";
        Title = Branding.DefaultWelcomeTitle;
        Greeting = "欢迎回来";
        Maximize = true;
    }

    [ScriptIgnore]
    public string AppName { get { return ApplicationLabel ?? Path.GetFileNameWithoutExtension(Target); } }

    private static readonly string Config = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Branding.ProfileFolder, "settings.json");

    public static IntroSettings Load()
    {
        try
        {
            if (File.Exists(Config))
                return new JavaScriptSerializer().Deserialize<IntroSettings>(File.ReadAllText(Config)) ?? new IntroSettings();
        }
        catch (IOException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
        return new IntroSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config));
        string temporary = Config + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(Config)) File.Replace(temporary, Config, null);
            else File.Move(temporary, Config);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public bool HasTarget()
    {
        string extension = Path.GetExtension(Target ?? "");
        return File.Exists(Target) && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase));
    }

    public bool ReadPicture(out byte[] bytes, out string contentType)
    {
        bytes = null;
        contentType = null;
        if (string.IsNullOrWhiteSpace(Picture)) return true;
        try
        {
            using (var stream = File.OpenRead(Picture))
            {
                if (stream.Length > 20 * 1024 * 1024) return false;
                using (var image = Image.FromStream(stream, false, false))
                {
                    if (image.Width > 10000 || image.Height > 10000) return false;
                    if (image.RawFormat.Guid == ImageFormat.Png.Guid) contentType = "image/png";
                    else if (image.RawFormat.Guid == ImageFormat.Jpeg.Guid) contentType = "image/jpeg";
                    else return false;
                }
                stream.Position = 0;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    bytes = copy.ToArray();
                }
                return true;
            }
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (OutOfMemoryException) { return false; }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDpiAwareness(2); }
        catch (EntryPointNotFoundException) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var shortcuts = new ShortcutStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.ProfileFolder, "shortcuts"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Application.ExecutablePath);
        if (Array.IndexOf(args, "--restore") >= 0)
        {
            Application.Run(new RestoreWindow(shortcuts));
            return;
        }
        if (args.Length == 2 && args[0] == "--shortcut")
        {
            try
            {
                var profile = shortcuts.ForPlayback(args[1]);
                if (profile != null && profile.HasTarget()) { Play(profile); return; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show("找不到这个快捷方式对应的软件入口。可打开「撤销片头」恢复原快捷方式。",
                Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var settings = IntroSettings.Load();
        if (Array.IndexOf(args, "--play") >= 0 && settings.HasTarget())
        {
            try { Play(shortcuts.ForPreview(settings)); }
            catch (InvalidOperationException error) { MessageBox.Show(error.Message, Branding.ProductName); }
        }
        else Application.Run(new SettingsWindow(settings, shortcuts));
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);

    public static void Play(IntroSettings settings)
    {
        using (var mutex = new Mutex(false, @"Local\NianAnIntro.Playback"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                MessageBox.Show("片头正在播放。", Branding.ProductName);
                return;
            }
            try { LaunchSession.Play(settings); }
            catch (Exception)
            {
                MessageBox.Show("片头无法启动。请确认 Microsoft Edge 或 Google Chrome 可以正常打开，再试一次。",
                    Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

internal sealed class SettingsWindow : Form
{
    private readonly TextBox target = new TextBox { ReadOnly = true };
    private readonly TextBox title = new TextBox { MaxLength = 16 };
    private readonly TextBox greeting = new TextBox { MaxLength = 42 };
    private readonly TextBox picture = new TextBox { ReadOnly = true };
    private readonly TextBox windowTitle = new TextBox { MaxLength = 64 };
    private readonly ShortcutStore shortcuts;
    private readonly CheckBox maximize = new CheckBox { Text = "切换前最大化支持最大化的软件", AutoSize = true };
    private readonly Label status = new Label
    {
        Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(64, 90, 118)
    };

    public SettingsWindow(IntroSettings settings, ShortcutStore shortcuts)
    {
        this.shortcuts = shortcuts;
        Text = Branding.ProductName;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(650, 414);
        BackColor = Color.FromArgb(245, 249, 253);
        ForeColor = Color.FromArgb(37, 62, 87);
        target.Text = settings.Target;
        title.Text = settings.Title;
        greeting.Text = settings.Greeting;
        picture.Text = settings.Picture;
        windowTitle.Text = settings.WindowTitle;
        maximize.Checked = settings.Maximize;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
        for (int i = 0; i < 10; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 56 : 36));

        var heading = new Label
        {
            Text = "启动片头设置", Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        grid.Controls.Add(heading, 0, 0);
        grid.SetColumnSpan(heading, 3);
        AddField(grid, 1, "目标软件", target, "浏览…", () => PickTarget());
        AddField(grid, 2, "欢迎标题", title);
        AddField(grid, 3, "欢迎语", greeting);
        AddField(grid, 4, "欢迎页背景", picture, "选择图片…", () => PickPicture());
        AddField(grid, 5, "窗口标题关键词", windowTitle);
        var guidance = new Label
        {
            Text = "可选。软件通过启动器打开、无法自动识别窗口时填写。",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        grid.Controls.Add(guidance, 1, 6);
        grid.SetColumnSpan(guidance, 2);
        maximize.Anchor = AnchorStyles.Left;
        grid.Controls.Add(maximize, 1, 7);
        grid.SetColumnSpan(maximize, 2);
        grid.Controls.Add(status, 0, 8);
        grid.SetColumnSpan(status, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(MakeButton("保存并播放", () => SaveAndPlay(), 108));
        var apply = MakeButton("创建桌面快捷方式", () => CreateShortcut(), 155);
        Action updateApplyLabel = () => {
            apply.Text = shortcuts.IsDesktopLink(target.Text) ? "套用到原快捷方式" : "创建桌面快捷方式";
        };
        target.TextChanged += (sender, args) => updateApplyLabel();
        updateApplyLabel();
        actions.Controls.Add(apply);
        actions.Controls.Add(MakeButton("使用默认背景", () => { picture.Clear(); Save(); }, 124));
        grid.Controls.Add(actions, 0, 9);
        grid.SetColumnSpan(actions, 3);
        Controls.Add(grid);
    }

    private static Button MakeButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 29, UseVisualStyleBackColor = true };
        button.Click += (sender, args) => action();
        return button;
    }

    private static void AddField(TableLayoutPanel grid, int row, string label, TextBox field,
        string buttonText = null, Action picker = null)
    {
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(field, 1, row);
        if (picker != null) grid.Controls.Add(MakeButton(buttonText, picker, 96), 2, row);
        else grid.SetColumnSpan(field, 2);
    }

    private void PickTarget()
    {
        using (var dialog = new OpenFileDialog
        {
            Title = "选择要启动的软件",
            Filter = "Windows 程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk",
            DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            target.Text = dialog.FileName;
            windowTitle.Clear();
            status.Text = "";
        }
    }

    private void PickPicture()
    {
        using (var dialog = new OpenFileDialog { Title = "选择欢迎页图片", Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            byte[] bytes;
            string type;
            var selected = new IntroSettings { Picture = dialog.FileName };
            if (!selected.ReadPicture(out bytes, out type))
                status.Text = "请选择可以打开的 PNG 或 JPG 图片，大小不超过 20 MB。";
            else
            {
                picture.Text = dialog.FileName;
                status.Text = "图片已选中，保存后生效。";
            }
        }
    }

    private IntroSettings Save()
    {
        var settings = new IntroSettings {
            Target = target.Text.Trim(), Title = title.Text.Trim(), Greeting = greeting.Text.Trim(),
            Picture = picture.Text.Trim(), WindowTitle = windowTitle.Text.Trim(), Maximize = maximize.Checked
        };
        if (!settings.HasTarget())
        {
            status.Text = "请先选择一个存在的软件或快捷方式。";
            return null;
        }
        if (settings.Title.Length == 0) settings.Title = Branding.DefaultWelcomeTitle;
        try { settings.Save(); }
        catch (IOException)
        {
            status.Text = "无法写入本机设置。";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            status.Text = "无法写入本机设置。";
            return null;
        }
        status.Text = "已保存。";
        return settings;
    }

    private void SaveAndPlay()
    {
        var settings = Save();
        if (settings == null) return;
        try { settings = shortcuts.ForPreview(settings); }
        catch (InvalidOperationException error) { status.Text = error.Message; return; }
        Hide();
        try { Program.Play(settings); }
        finally { Close(); }
    }

    private void CreateShortcut()
    {
        var settings = Save();
        if (settings == null) return;
        try
        {
            var record = shortcuts.Apply(settings);
            string name = Path.GetFileNameWithoutExtension(record.PathOnDesktop);
            status.Text = record.OriginalHash == null ? "已配置桌面入口「" + name + "」。"
                : "已在「" + name + "」套用片头，原快捷方式已备份。";
        }
        catch (InvalidOperationException error) { status.Text = error.Message; }
        catch (Exception)
        {
            status.Text = "无法创建桌面快捷方式，可以直接打开本程序播放。";
        }
    }
}
