using System.Drawing.Drawing2D;

namespace WinStyleStudio;

class CheckerBox : PictureBox
{
    static readonly Bitmap Tile = MakeTile();
    static Bitmap MakeTile()
    {
        var b = new Bitmap(16, 16);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.White);
        using var br = new SolidBrush(Color.FromArgb(222, 222, 222));
        g.FillRectangle(br, 0, 0, 8, 8);
        g.FillRectangle(br, 8, 8, 8, 8);
        return b;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using (var tb = new TextureBrush(Tile)) e.Graphics.FillRectangle(tb, ClientRectangle);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        base.OnPaint(e);
    }
}

/// <summary>
/// Editor: every change is STAGED in memory only. Nothing touches Windows until
/// "Install & Apply…" is clicked, confirmed, and a backup has been made.
/// </summary>
class EditorTab : UserControl
{
    StyleFile? style;
    ResEntry? sel;
    Bitmap? orig, edited;
    readonly Dictionary<ResEntry, TreeNode> nodes = new();

    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter by type or name…" };
    readonly CheckBox imgOnly = new() { Text = "Images only", Checked = true, Dock = DockStyle.Top, Height = 24 };
    readonly CheckerBox picO = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    readonly CheckerBox picE = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    readonly Label lblO = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label lblE = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 4, 0, 0), BackColor = Color.FromArgb(255, 244, 214) };
    readonly TrackBar hue = new() { Minimum = -180, Maximum = 180, TickFrequency = 30, Width = 260 };
    readonly TrackBar sat = new() { Minimum = -100, Maximum = 100, TickFrequency = 25, Width = 260 };
    readonly TrackBar light = new() { Minimum = -100, Maximum = 100, TickFrequency = 25, Width = 260 };
    readonly CheckBox colorize = new() { Text = "Colorize to:", AutoSize = true };
    readonly Button colorBtn = new() { Width = 60, Height = 24, BackColor = Color.SteelBlue, FlatStyle = FlatStyle.Flat };
    readonly CheckBox keepNeutral = new() { Text = "Keep neutral (gray/white) pixels", Checked = true, AutoSize = true };

    public EditorTab()
    {
        // top bar
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 6, 6, 0) };
        top.Controls.Add(Btn("Open style file…", (_, _) => OpenDialog(), 120, false));
        top.Controls.Add(Btn("Open current Windows style", (_, _) => LoadStyle(ThemeTools.CurrentStylePath()), 170, false));
        top.Controls.Add(Btn("Export edited .msstyles…", (_, _) => ExportStyle(), 160, false));
        top.Controls.Add(Btn("Export all images (PNG)…", (_, _) => ExportImages(), 160, false));
        var apply = Btn("Install && Apply…", (_, _) => { if (style != null) Installer.Run(this, style); }, 130, false);
        apply.BackColor = Color.FromArgb(255, 224, 178);
        top.Controls.Add(apply);

        // left: tree
        var left = new Panel { Dock = DockStyle.Left, Width = 330 };
        left.Controls.Add(tree);
        left.Controls.Add(imgOnly);
        left.Controls.Add(filter);

        // center: before / after
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        center.Controls.Add(lblO, 0, 0); center.Controls.Add(lblE, 1, 0);
        center.Controls.Add(picO, 0, 1); center.Controls.Add(picE, 1, 1);

        // right: adjustments
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 290, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        right.Controls.Add(Hdr("Adjust (always relative to the ORIGINAL image)"));
        right.Controls.Add(Hdr("Hue shift (°)")); right.Controls.Add(hue);
        right.Controls.Add(Hdr("Saturation (%)")); right.Controls.Add(sat);
        right.Controls.Add(Hdr("Lightness (%)")); right.Controls.Add(light);
        var cz = new FlowLayoutPanel { Width = 260, Height = 30 };
        cz.Controls.Add(colorize); cz.Controls.Add(colorBtn);
        right.Controls.Add(cz);
        right.Controls.Add(keepNeutral);
        right.Controls.Add(Btn("Reset sliders", (_, _) => ResetSliders(), 260));
        right.Controls.Add(Btn("Stage on SELECTED image", (_, _) => StageSelected(), 260));
        right.Controls.Add(Btn("Stage on ALL listed PNG images", (_, _) => StageAll(), 260));
        right.Controls.Add(Btn("Replace selected from PNG file…", (_, _) => ReplaceFromFile(), 260));
        right.Controls.Add(Btn("Revert selected", (_, _) => RevertSelected(), 260));
        right.Controls.Add(Btn("Revert ALL staged changes", (_, _) => RevertAll(), 260));

        Controls.Add(center);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(status);
        Controls.Add(top);

        tree.AfterSelect += (_, e) => SelectEntry(e.Node);
        filter.TextChanged += (_, _) => BuildTree();
        imgOnly.CheckedChanged += (_, _) => BuildTree();
        foreach (var tb in new[] { hue, sat, light }) tb.ValueChanged += (_, _) => UpdatePreview();
        colorize.CheckedChanged += (_, _) => UpdatePreview();
        keepNeutral.CheckedChanged += (_, _) => UpdatePreview();
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = colorBtn.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) { colorBtn.BackColor = dlg.Color; colorize.Checked = true; UpdatePreview(); }
        };
        UpdateStatus();
    }

    static Button Btn(string text, EventHandler click, int w, bool margin = true)
    {
        var b = new Button { Text = text, Width = w, Height = 30 };
        if (margin) b.Margin = new Padding(0, 4, 0, 4);
        b.Click += click;
        return b;
    }
    static Label Hdr(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 8, 0, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

    bool AdjustActive => hue.Value != 0 || sat.Value != 0 || light.Value != 0 || colorize.Checked;
    Color? Target => colorize.Checked ? colorBtn.BackColor : null;
    Bitmap MakeAdjusted(Bitmap src) => Imaging.Adjust(src, hue.Value, sat.Value, light.Value, Target, keepNeutral.Checked);

    // ───────── loading / tree ─────────
    void OpenDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Visual styles (*.msstyles)|*.msstyles|All files|*.*", InitialDirectory = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Resources\Themes") };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadStyle(dlg.FileName);
    }

    public void LoadStyle(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            var s = new StyleFile(path);
            picO.Image = null; picE.Image = null; orig?.Dispose(); edited?.Dispose(); orig = edited = null; sel = null;
            style?.Dispose();
            style = s;
            BuildTree();
            UpdateStatus();
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not open style:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    void BuildTree()
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        nodes.Clear();
        if (style != null)
        {
            var f = filter.Text.Trim();
            var list = style.Entries
                .Where(e => !imgOnly.Checked || e.IsImage)
                .Where(e => f.Length == 0 || $"{e.Type}/{e.Name}".Contains(f, StringComparison.OrdinalIgnoreCase));
            foreach (var g in list.GroupBy(e => e.Type))
            {
                var tn = new TreeNode($"{g.Key}  ({g.Count()})");
                foreach (var e in g)
                {
                    var n = new TreeNode($"{e.Name}   [{e.Kind}, {e.Size:N0} B]") { Tag = e };
                    if (style.Pending.ContainsKey(e)) n.ForeColor = Color.DarkOrange;
                    tn.Nodes.Add(n);
                    nodes[e] = n;
                }
                tree.Nodes.Add(tn);
            }
        }
        tree.EndUpdate();
    }

    void SelectEntry(TreeNode? node)
    {
        if (style == null || node?.Tag is not ResEntry e) return;
        sel = e;
        picO.Image = null; picE.Image = null; orig?.Dispose(); orig = null;
        if (!e.IsImage)
        {
            lblO.Text = $"{e.Type}/{e.Name}: binary data ({e.Size:N0} B) - compiled style definition, view only.";
            lblE.Text = "";
            return;
        }
        try { orig = Imaging.Decode(style.ReadOriginal(e)); }
        catch { lblO.Text = "Could not decode this image."; return; }
        picO.Image = orig;
        lblO.Text = $"Original  {e.Type}/{e.Name}  {orig.Width}×{orig.Height}  ({e.Kind})";
        UpdatePreview();
    }

    void UpdatePreview()
    {
        if (style == null || sel == null || orig == null) return;
        Bitmap next; string note;
        if (AdjustActive) { next = MakeAdjusted(orig); note = "Preview of adjustment (not staged yet)"; }
        else if (sel.IsPng && style.Pending.TryGetValue(sel, out var b)) { next = Imaging.Decode(b); note = "Staged edit"; }
        else { next = new Bitmap(orig); note = "Unchanged"; }
        picE.Image = null; edited?.Dispose(); edited = next; picE.Image = edited;
        lblE.Text = note + (sel.IsPng ? "" : "  -  BMP resources are view-only");
    }

    void UpdateStatus() =>
        status.Text = style == null
            ? "No style loaded. Open one above.  Nothing is applied to Windows until you use Install && Apply and confirm."
            : $"{style.FilePath}   |   {style.Entries.Count} resources, {style.Entries.Count(e => e.IsImage)} images   |   {style.Pending.Count} staged edit(s) - NOT applied";

    void Mark(ResEntry e, bool staged) { if (nodes.TryGetValue(e, out var n)) n.ForeColor = staged ? Color.DarkOrange : tree.ForeColor; }

    void ResetSliders() { hue.Value = 0; sat.Value = 0; light.Value = 0; colorize.Checked = false; UpdatePreview(); }

    // ───────── staging (memory only) ─────────
    void StageSelected()
    {
        if (style == null || sel == null || orig == null) return;
        if (!sel.IsPng) { MessageBox.Show(this, "Only PNG resources can be edited. This one is " + sel.Kind + " (view only).", "Not editable"); return; }
        if (!AdjustActive) { MessageBox.Show(this, "Move a slider or enable Colorize first.", "No adjustment"); return; }
        using var adj = MakeAdjusted(orig);
        style.Pending[sel] = Imaging.EncodePng(adj);
        Mark(sel, true);
        UpdatePreview(); UpdateStatus();
    }

    void StageAll()
    {
        if (style == null) return;
        if (!AdjustActive) { MessageBox.Show(this, "Move a slider or enable Colorize first.", "No adjustment"); return; }
        var targets = nodes.Keys.Where(e => e.IsPng).ToList();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this, $"Stage this adjustment on {targets.Count} PNG image(s) in the current list?\n\nThis only stages changes in memory - nothing is applied to Windows.",
                "Stage on all", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            foreach (var e in targets)
            {
                using var src = Imaging.Decode(style.ReadOriginal(e));
                using var adj = MakeAdjusted(src);
                style.Pending[e] = Imaging.EncodePng(adj);
                Mark(e, true);
            }
        }
        finally { Cursor = Cursors.Default; }
        UpdatePreview(); UpdateStatus();
    }

    void ReplaceFromFile()
    {
        if (style == null || sel == null || orig == null || !sel.IsPng) { MessageBox.Show(this, "Select a PNG resource first.", "Replace"); return; }
        using var dlg = new OpenFileDialog { Filter = "PNG image|*.png" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        using var img = Imaging.Decode(File.ReadAllBytes(dlg.FileName));
        if (img.Size != orig.Size)
        {
            MessageBox.Show(this, $"Size mismatch. The style expects {orig.Width}×{orig.Height}, your file is {img.Width}×{img.Height}.", "Wrong size");
            return;
        }
        style.Pending[sel] = Imaging.EncodePng(img);
        Mark(sel, true); UpdatePreview(); UpdateStatus();
    }

    void RevertSelected()
    {
        if (style == null || sel == null) return;
        style.Pending.Remove(sel); Mark(sel, false); UpdatePreview(); UpdateStatus();
    }

    void RevertAll()
    {
        if (style == null || style.Pending.Count == 0) return;
        if (MessageBox.Show(this, "Discard all staged edits?", "Revert all", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        style.Pending.Clear(); BuildTree(); UpdatePreview(); UpdateStatus();
    }

    // ───────── exporters (write files you choose; never touch Windows) ─────────
    void ExportStyle()
    {
        if (style == null) return;
        using var dlg = new SaveFileDialog { Filter = "Visual style (*.msstyles)|*.msstyles", FileName = "Custom.msstyles" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { style.Save(dlg.FileName); MessageBox.Show(this, "Exported a copy with your staged edits:\n" + dlg.FileName + "\n\nWindows was not changed.", "Exported"); }
        catch (Exception ex) { MessageBox.Show(this, "Export failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    void ExportImages()
    {
        if (style == null) return;
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder for the exported PNG files" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int count = 0;
        try
        {
            Cursor = Cursors.WaitCursor;
            foreach (var e in style.Entries.Where(x => x.IsImage))
            {
                var dir = Path.Combine(dlg.SelectedPath, Safe(e.Type));
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"{Safe(e.Name)}_{e.Lang}.png");
                var bytes = style.ReadCurrent(e);
                if (e.IsPng) File.WriteAllBytes(file, bytes);
                else { using var bmp = Imaging.Decode(bytes); File.WriteAllBytes(file, Imaging.EncodePng(bmp)); }
                count++;
            }
        }
        finally { Cursor = Cursors.Default; }
        MessageBox.Show(this, $"Exported {count} image(s).", "Exported");
    }
}
