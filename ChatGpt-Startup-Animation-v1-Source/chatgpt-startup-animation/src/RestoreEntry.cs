using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class RestoreEntry
{
    [STAThread]
    private static void Main()
    {
        string launcher = Path.Combine(Application.StartupPath, Branding.LauncherFile);
        try
        {
            if (File.Exists(launcher))
            {
                using (Process.Start(new ProcessStartInfo { FileName = launcher, Arguments = "--restore", UseShellExecute = false }))
                { }
                return;
            }
        }
        catch (Exception) { }
        MessageBox.Show("请把「撤销片头.exe」和「" + Branding.LauncherFile + "」放在同一个文件夹，再打开。",
            Branding.UndoTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
