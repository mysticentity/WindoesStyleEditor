using System.Diagnostics;

namespace WinStyleStudio;

static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string text, string def)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(420, 120)
        };
        var lbl = new Label { Left = 12, Top = 12, Width = 396, Height = 32, Text = text };
        var tb = new TextBox { Left = 12, Top = 48, Width = 396, Text = def };
        var ok = new Button { Text = "OK", Left = 242, Top = 80, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 328, Top = 80, Width = 80, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}

class BackupsTab : UserControl
{
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };

    public BackupsTab()
    {
        list.Columns.Add("Created", 150);
        list.Columns.Add("Reason", 300);
        list.Columns.Add("Previous theme", 420);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 6, 6, 0) };
        bar.Controls.Add(MakeBtn("Create backup now", (_, _) => { Backup.Create("manual backup"); Reload(); }, 140));
        bar.Controls.Add(MakeBtn("Restore selected…", (_, _) => RestoreSelected(), 140));
        bar.Controls.Add(MakeBtn("Open backup folder", (_, _) =>
        {
            Directory.CreateDirectory(Backup.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Backup.Root}\"") { UseShellExecute = true });
        }, 140));
        bar.Controls.Add(MakeBtn("Refresh", (_, _) => Reload(), 80));

        Controls.Add(list);
        Controls.Add(bar);
        VisibleChanged += (_, _) => { if (Visible) Reload(); };
    }

    static Button MakeBtn(string t, EventHandler h, int w) { var b = new Button { Text = t, Width = w, Height = 30 }; b.Click += h; return b; }

    void Reload()
    {
        list.Items.Clear();
        foreach (var b in Backup.List())
            list.Items.Add(new ListViewItem(new[] { b.Created.ToString("g"), b.Reason, b.PrevTheme }) { Tag = b });
    }

    void RestoreSelected()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not BackupInfo b) return;
        if (MessageBox.Show(this,
                $"Restore the backup from {b.Created:g}?\n\n{b.Reason}\n\nYour current state is saved as a new backup first.",
                "Confirm restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { MessageBox.Show(this, Backup.Restore(b.Dir), "Restored"); Reload(); }
        catch (Exception ex) { MessageBox.Show(this, "Restore failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

class MainForm : Form
{
    public MainForm()
    {
        Text = "Windows Style Studio";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        var banner = new Label
        {
            Dock = DockStyle.Top, Height = 28, Padding = new Padding(10, 6, 0, 0), BackColor = Color.FromArgb(222, 240, 222),
            Text = "SAFE MODE: browsing and editing only stage changes. Windows is untouched until you click Install && Apply, confirm, and a backup is made."
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var explorer = new TabPage("Visual Style Explorer (read-only)");
        var editor = new TabPage("Style Editor && Exporter");
        var backups = new TabPage("Backups && Restore");
        explorer.Controls.Add(new ExplorerTab { Dock = DockStyle.Fill });
        editor.Controls.Add(new EditorTab { Dock = DockStyle.Fill });
        backups.Controls.Add(new BackupsTab { Dock = DockStyle.Fill });
        tabs.TabPages.AddRange(new[] { explorer, editor, backups });   // Explorer is first = default

        Controls.Add(tabs);
        Controls.Add(banner);
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
