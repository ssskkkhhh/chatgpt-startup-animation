using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class RestoreWindow : Form
{
    private readonly ShortcutStore shortcuts;
    private readonly ListBox links = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label notice = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button restore = new Button { Text = "撤销所选", AutoSize = true };

    public RestoreWindow(ShortcutStore shortcuts)
    {
        this.shortcuts = shortcuts;
        Text = Branding.UndoTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 310);
        ClientSize = new Size(550, 295);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 249, 253);
        ForeColor = Color.FromArgb(37, 62, 87);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 16), RowCount = 4, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.Controls.Add(new Label
        {
            Text = "选择要撤销片头的快捷方式", Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        layout.Controls.Add(links, 0, 1);
        layout.Controls.Add(notice, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var refresh = new Button { Text = "刷新", AutoSize = true };
        buttons.Controls.Add(restore);
        buttons.Controls.Add(refresh);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        links.SelectedIndexChanged += (sender, args) => restore.Enabled = links.SelectedItem != null;
        refresh.Click += (sender, args) => LoadShortcuts();
        restore.Click += (sender, args) => UndoSelected();
        LoadShortcuts();
    }

    private void LoadShortcuts()
    {
        links.Items.Clear();
        restore.Enabled = false;
        try
        {
            foreach (var item in shortcuts.ListForUndo()) links.Items.Add(item);
            notice.Text = links.Items.Count == 0
                ? "没有可撤销的快捷方式。"
                : "选择一个入口，就可以恢复原快捷方式或移除新建入口。";
        }
        catch (Exception)
        {
            notice.Text = "无法读取本机的快捷方式备份。";
        }
    }

    private void UndoSelected()
    {
        var item = links.SelectedItem as ShortcutRecord;
        if (item == null) return;
        try
        {
            shortcuts.Undo(item.Id);
            LoadShortcuts();
            string name = System.IO.Path.GetFileNameWithoutExtension(item.PathOnDesktop);
            notice.Text = item.OriginalHash == null ? "已移除「" + name + "」。" : "已恢复「" + name + "」。";
        }
        catch (InvalidOperationException error) { notice.Text = error.Message; }
        catch (Exception) { notice.Text = "无法撤销。请确认桌面快捷方式可以写入，再试一次。"; }
    }
}
