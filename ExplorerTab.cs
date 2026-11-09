using System.Reflection;
using System.Windows.Forms.VisualStyles;

namespace WinStyleStudio;

/// <summary>
/// READ-ONLY. Asks the active visual style (uxtheme) to draw every class / part / state it defines,
/// so you can see exactly how Windows renders each element. Nothing here changes the system.
/// </summary>
class ExplorerTab : UserControl
{
    record StateDef(string Name, VisualStyleElement El);
    record PartDef(string Name, List<StateDef> States);

    readonly Dictionary<string, List<PartDef>> classes = new();
    readonly TreeView tree = new() { Dock = DockStyle.Left, Width = 240, HideSelection = false };
    readonly Panel host = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Label info = new() { Dock = DockStyle.Top, Height = 78, Padding = new Padding(10, 6, 10, 0), BackColor = Color.FromArgb(235, 242, 252) };
    readonly List<Bitmap> bitmaps = new();
    readonly ToolTip tip = new() { InitialDelay = 200 };
    bool loaded;

    const int TW = 124, TH = 44;

    public ExplorerTab()
    {
        var refresh = new Button { Text = "Refresh", Width = 80, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refresh.Click += (_, _) => { loaded = false; Load(); };
        info.Controls.Add(refresh);
        refresh.Left = info.Width - 100;
        info.Resize += (_, _) => refresh.Left = info.Width - 100;
        refresh.Top = 8;

        tree.AfterSelect += (_, e) => { if (e.Node?.Tag is string cls) ShowClass(cls); };
        host.Resize += (_, _) => { if (tree.SelectedNode?.Tag is string cls) ShowClass(cls); };

        Controls.Add(host);
        Controls.Add(tree);
        Controls.Add(info);
        VisibleChanged += (_, _) => { if (Visible && !loaded) Load(); };
    }

    void Load()
    {
        loaded = true;
        classes.Clear();
        tree.Nodes.Clear();
        host.Controls.Clear();

        if (!VisualStyleRenderer.IsSupported)
        {
            info.Text = "Visual styles are not active for this process (or a High Contrast / classic theme is in use). The explorer needs an active visual style.";
            return;
        }

        info.Text =
            $"Active visual style:  {VisualStyleInformation.DisplayName}   |   Author: {VisualStyleInformation.Author}   |   Company: {VisualStyleInformation.Company}\r\n" +
            $"Color scheme: {VisualStyleInformation.ColorScheme}   |   Size: {VisualStyleInformation.Size}   |   Enabled by user: {VisualStyleInformation.IsEnabledByUser}\r\n" +
            "READ-ONLY view - each tile is drawn live by Windows from the active .msstyles. Nothing here modifies the system.";

        foreach (var cls in typeof(VisualStyleElement).GetNestedTypes(BindingFlags.Public).OrderBy(c => c.Name))
        {
            var parts = new List<PartDef>();
            foreach (var part in cls.GetNestedTypes(BindingFlags.Public))
            {
                var states = new List<StateDef>();
                foreach (var prop in part.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(VisualStyleElement)))
                {
                    try
                    {
                        var el = (VisualStyleElement)prop.GetValue(null)!;
                        if (VisualStyleRenderer.IsElementDefined(el)) states.Add(new StateDef(prop.Name, el));
                    }
                    catch { /* element not available on this OS build */ }
                }
                if (states.Count > 0) parts.Add(new PartDef(part.Name, states));
            }
            if (parts.Count == 0) continue;
            classes[cls.Name] = parts;
            tree.Nodes.Add(new TreeNode($"{cls.Name}  ({parts.Count} parts)") { Tag = cls.Name });
        }
        if (tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0];
    }

    void ShowClass(string cls)
    {
        host.SuspendLayout();
        foreach (Control c in host.Controls.Cast<Control>().ToList()) { host.Controls.Remove(c); c.Dispose(); }
        foreach (var b in bitmaps) b.Dispose();
        bitmaps.Clear();

        int width = Math.Max(300, host.ClientSize.Width - 30);
        int perRow = Math.Max(1, (width - 20) / (TW + 8));
        int y = 8;

        foreach (var part in classes[cls])
        {
            int rows = (int)Math.Ceiling(part.States.Count / (double)perRow);
            var gb = new GroupBox { Text = $"{cls}  ›  {part.Name}", Left = 8, Top = y, Width = width, Height = 26 + rows * (TH + 24) + 6 };
            for (int i = 0; i < part.States.Count; i++)
            {
                var st = part.States[i];
                int cx = 10 + (i % perRow) * (TW + 8), cy = 22 + (i / perRow) * (TH + 24);
                var bmp = Render(st.El);
                bitmaps.Add(bmp);
                var pb = new PictureBox { Left = cx, Top = cy, Width = TW, Height = TH, Image = bmp, BorderStyle = BorderStyle.FixedSingle };
                var lbl = new Label { Left = cx, Top = cy + TH + 2, Width = TW, Height = 16, Text = st.Name, Font = new Font("Segoe UI", 8f), AutoEllipsis = true };
                tip.SetToolTip(pb, $"{st.El.ClassName}\r\nPart {st.El.Part}, State {st.El.State}");
                gb.Controls.Add(pb);
                gb.Controls.Add(lbl);
            }
            host.Controls.Add(gb);
            y += gb.Height + 8;
        }
        host.ResumeLayout();
    }

    static Bitmap Render(VisualStyleElement el)
    {
        var bmp = new Bitmap(TW, TH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(243, 243, 243));
        try
        {
            var r = new VisualStyleRenderer(el);
            Size sz = Size.Empty;
            try { sz = r.GetPartSize(g, ThemeSizeType.True); } catch { }
            var rect = (sz.Width > 0 && sz.Height > 0 && sz.Width <= TW && sz.Height <= TH)
                ? new Rectangle((TW - sz.Width) / 2, (TH - sz.Height) / 2, sz.Width, sz.Height)
                : new Rectangle(2, 2, TW - 4, TH - 4);
            r.DrawBackground(g, rect);
        }
        catch
        {
            TextRenderer.DrawText(g, "n/a", SystemFonts.DefaultFont, new Rectangle(0, 0, TW, TH), Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return bmp;
    }
}
