using System.Diagnostics.CodeAnalysis;

using Helldivers2ModManager.Core.Security;

namespace Purger;

public partial class MainForm : Form
{
    private readonly SharedSafePathPolicy _safePathPolicy = new();
    public MainForm()
    {
        InitializeComponent();
    }

    private bool ValdiatePath(string path, [NotNullWhen(false)] out string? err)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!_safePathPolicy.IsUnderRoot(path, path))
            {
                err = "The selected game directory is a symbolic link or junction.";
                return false;
            }

            var dir = new DirectoryInfo(path);
            var dirs = dir.GetDirectories();

            var binDir = new DirectoryInfo(_safePathPolicy.ResolveUnderRoot(path, "bin"));
            if (!binDir.Exists)
            {
                err = "The selected folder does not have a directory named \"bin\"!";
                return false;
            }

            if (!binDir.GetFiles().Any(static f => f.Name == "helldivers2.exe"))
            {
                err = "The selected folders \"bin\" folder does not contain a file called \"helldivers2.exe\"!";
                return false;
            }

            if (!Directory.Exists(_safePathPolicy.ResolveUnderRoot(path, "data")))
            {
                err = "The selected folder does not have a directory named \"data\"!";
                return false;
            }

            if (!Directory.Exists(_safePathPolicy.ResolveUnderRoot(path, "tools")))
            {
                err = "The selected folder does not have a directory named \"tools\"!";
                return false;
            }

            err = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            err = ex.Message;
            return false;
        }
    }

    private void SafeInvoke(Action action)
    {
        if (InvokeRequired)
            Invoke(action);
    }

    async void btnPurge_Click(object sender, EventArgs e)
    {
        btnBrowse.Enabled = false;
        btnPurge.Enabled = false;
        progressBar.Value = 0;

        var count = await Task.Run(() =>
        {
            var dataPath = _safePathPolicy.ResolveUnderRoot(txtGameDir.Text, "data");
            var dir = new DirectoryInfo(dataPath);
            var files = dir.EnumerateFiles("*.patch_*").ToArray();

            if (files.Any(file => !_safePathPolicy.IsUnderRoot(dataPath, file.FullName)))
                throw new IOException("A purge target escaped the protected game data directory.");

            SafeInvoke(() => progressBar.Maximum = files.Length);

            foreach (var f in files)
            {
                f.Delete();
                SafeInvoke(() => progressBar.Value++);
            }

            return files.Length;
        });

        MessageBox.Show(this, $"删除 {count} 文件!\n建议验证游戏完整性.\n在Steam游戏库右键属性、已安装文件、点击验证游戏文件的完整性", "信息", MessageBoxButtons.OK, MessageBoxIcon.Information);

        btnBrowse.Enabled = true;
        btnPurge.Enabled = true;
    }

    void btnBrowse_Click(object sender, EventArgs e)
    {
        if (folderDialog.ShowDialog() != DialogResult.OK)
            return;

        if (!ValdiatePath(folderDialog.SelectedPath, out var err))
        {
            MessageBox.Show(this, err, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        txtGameDir.Text = folderDialog.SelectedPath;
        btnPurge.Enabled = true;
    }
}
