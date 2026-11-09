using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinStyleStudio;

// ───────────────────────── Win32 ─────────────────────────
static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);
    public static void BroadcastThemeChange() =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x1A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string file, IntPtr hFile, uint flags);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr h);

    public delegate bool EnumTypeProc(IntPtr h, IntPtr type, IntPtr lp);
    public delegate bool EnumNameProc(IntPtr h, IntPtr type, IntPtr name, IntPtr lp);
    public delegate bool EnumLangProc(IntPtr h, IntPtr type, IntPtr name, ushort lang, IntPtr lp);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceTypesW", SetLastError = true)]
    public static extern bool EnumResourceTypes(IntPtr h, EnumTypeProc p, IntPtr lp);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    public static extern bool EnumResourceNames(IntPtr h, IntPtr type, EnumNameProc p, IntPtr lp);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesW", SetLastError = true)]
    public static extern bool EnumResourceLanguages(IntPtr h, IntPtr type, IntPtr name, EnumLangProc p, IntPtr lp);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceExW", SetLastError = true)]
    public static extern IntPtr FindResourceEx(IntPtr h, IntPtr type, IntPtr name, ushort lang);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr g);
    [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr h, IntPtr res);

    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    public static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint cb);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    public static extern bool EndUpdateResource(IntPtr h, bool discard);
}

// ───────────────────────── .msstyles resource model ─────────────────────────
class ResEntry
{
    public string Type = "", Name = "", Kind = "binary";
    public ushort Lang;
    public int Size;
    public bool IsPng => Kind == "PNG";
    public bool IsImage => Kind == "PNG" || Kind == "BMP";
}

/// <summary>An .msstyles file opened read-only. Edits are only staged in memory (Pending) until Save() writes a COPY.</summary>
class StyleFile : IDisposable
{
    public readonly string FilePath;
    public readonly List<ResEntry> Entries = new();
    public readonly Dictionary<ResEntry, byte[]> Pending = new();
    IntPtr h;

    public StyleFile(string path)
    {
        FilePath = path;
        h = Native.LoadLibraryEx(path, IntPtr.Zero, 0x22); // AS_DATAFILE | AS_IMAGE_RESOURCE
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Enumerate();
    }

    static string IdToStr(IntPtr p) => ((long)p >> 16) == 0 ? "#" + (int)p : Marshal.PtrToStringUni(p)!;

    static IntPtr StrToId(string s, List<IntPtr> allocs)
    {
        if (s.StartsWith('#') && int.TryParse(s.AsSpan(1), out int n)) return (IntPtr)n;
        var p = Marshal.StringToHGlobalUni(s);
        allocs.Add(p);
        return p;
    }

    static void Free(List<IntPtr> a) { foreach (var p in a) Marshal.FreeHGlobal(p); a.Clear(); }

    static string Sniff(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "PNG";
        if (d.Length > 54 && d[0] == (byte)'B' && d[1] == (byte)'M') return "BMP";
        return "binary";
    }

    void Enumerate()
    {
        var types = new List<string>();
        Native.EnumTypeProc tp = (x1, x2, x3) => { types.Add(IdToStr(x2)); return true; };
        Native.EnumResourceTypes(h, tp, IntPtr.Zero);
        GC.KeepAlive(tp);

        foreach (var type in types)
        {
            var al = new List<IntPtr>();
            var typePtr = StrToId(type, al);
            var names = new List<string>();
            Native.EnumNameProc np = (y1, y2, y3, y4) => { names.Add(IdToStr(y3)); return true; };
            Native.EnumResourceNames(h, typePtr, np, IntPtr.Zero);
            GC.KeepAlive(np);

            foreach (var name in names)
            {
                var al2 = new List<IntPtr>();
                var namePtr = StrToId(name, al2);
                var langs = new List<ushort>();
                Native.EnumLangProc lpr = (z1, z2, z3, z4, z5) => { langs.Add(z4); return true; };
                Native.EnumResourceLanguages(h, typePtr, namePtr, lpr, IntPtr.Zero);
                GC.KeepAlive(lpr);
                Free(al2);

                foreach (var lang in langs)
                {
                    var e = new ResEntry { Type = type, Name = name, Lang = lang };
                    var data = ReadOriginal(e);
                    e.Size = data.Length;
                    e.Kind = Sniff(data);
                    Entries.Add(e);
                }
            }
            Free(al);
        }
    }

    public byte[] ReadOriginal(ResEntry e)
    {
        var al = new List<IntPtr>();
        try
        {
            var res = Native.FindResourceEx(h, StrToId(e.Type, al), StrToId(e.Name, al), e.Lang);
            if (res == IntPtr.Zero) return Array.Empty<byte>();
            var glob = Native.LoadResource(h, res);
            var ptr = Native.LockResource(glob);
            var buf = new byte[(int)Native.SizeofResource(h, res)];
            Marshal.Copy(ptr, buf, 0, buf.Length);
            return buf;
        }
        finally { Free(al); }
    }

    /// <summary>Staged bytes if edited, otherwise the original.</summary>
    public byte[] ReadCurrent(ResEntry e) => Pending.TryGetValue(e, out var b) ? b : ReadOriginal(e);

    /// <summary>Writes a COPY of the style with staged edits to outPath. The source file is never modified.</summary>
    public void Save(string outPath)
    {
        File.Copy(FilePath, outPath, true);
        File.SetAttributes(outPath, FileAttributes.Normal);
        var uh = Native.BeginUpdateResource(outPath, false);
        if (uh == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var al = new List<IntPtr>();
        try
        {
            foreach (var (e, data) in Pending)
                if (!Native.UpdateResource(uh, StrToId(e.Type, al), StrToId(e.Name, al), e.Lang, data, (uint)data.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.EndUpdateResource(uh, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Native.EndUpdateResource(uh, true); throw; }
        finally { Free(al); }
    }

    public void Dispose() { if (h != IntPtr.Zero) { Native.FreeLibrary(h); h = IntPtr.Zero; } }
}

// ───────────────────────── Imaging ─────────────────────────
static class Imaging
{
    public static Bitmap Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    public static byte[] EncodePng(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static void ToHsl(int r, int g, int b, out float h, out float s, out float l)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        l = (max + min) / 2f;
        float d = max - min;
        if (d < 1e-6f) { h = 0; s = 0; return; }
        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (max == rf) h = (gf - bf) / d + (gf < bf ? 6 : 0);
        else if (max == gf) h = (bf - rf) / d + 2;
        else h = (rf - gf) / d + 4;
        h *= 60f;
    }

    static void FromHsl(float h, float s, float l, out byte r, out byte g, out byte b)
    {
        h = ((h % 360f) + 360f) % 360f / 60f;
        float c = (1f - Math.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Math.Abs(h % 2f - 1f));
        float m = l - c / 2f;
        float r1, g1, b1;
        if (h < 1) (r1, g1, b1) = (c, x, 0);
        else if (h < 2) (r1, g1, b1) = (x, c, 0);
        else if (h < 3) (r1, g1, b1) = (0, c, x);
        else if (h < 4) (r1, g1, b1) = (0, x, c);
        else if (h < 5) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);
        static byte B(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
        r = B(r1 + m); g = B(g1 + m); b = B(b1 + m);
    }

    /// <summary>Hue shift / saturation / lightness adjustment, or "colorize" to a target color. Alpha is preserved.</summary>
    public static Bitmap Adjust(Bitmap src, float hueShift, float satPct, float lightPct, Color? colorize, bool keepNeutral)
    {
        int w = src.Width, hgt = src.Height;
        var rect = new Rectangle(0, 0, w, hgt);
        var outBmp = new Bitmap(w, hgt, PixelFormat.Format32bppArgb);
        var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[Math.Abs(sd.Stride) * hgt];
        Marshal.Copy(sd.Scan0, buf, 0, buf.Length);

        float tH = 0, tS = 0, tL;
        if (colorize is Color t) ToHsl(t.R, t.G, t.B, out tH, out tS, out tL);

        for (int y = 0; y < hgt; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * sd.Stride + x * 4;
                if (buf[i + 3] == 0) continue;
                ToHsl(buf[i + 2], buf[i + 1], buf[i], out float H, out float S, out float L);
                bool neutral = S < 0.10f;
                if (!(keepNeutral && neutral))
                {
                    if (colorize != null) { H = tH; S = tS; }
                    else H += hueShift;
                }
                S = Math.Clamp(S * (1f + satPct / 100f), 0f, 1f);
                L = lightPct >= 0 ? L + (1f - L) * lightPct / 100f : L * (1f + lightPct / 100f);
                FromHsl(H, S, Math.Clamp(L, 0f, 1f), out byte r, out byte g, out byte b);
                buf[i + 2] = r; buf[i + 1] = g; buf[i] = b;
            }

        Marshal.Copy(buf, 0, dd.Scan0, buf.Length);
        src.UnlockBits(sd);
        outBmp.UnlockBits(dd);
        return outBmp;
    }
}

// ───────────────────────── Theme helpers ─────────────────────────
static class ThemeTools
{
    public static string? CurrentThemeFile()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes");
        return k?.GetValue("CurrentTheme") as string;
    }

    public static string CurrentStylePath()
    {
        var fallback = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Resources\Themes\aero\aero.msstyles");
        var tf = CurrentThemeFile();
        if (tf != null && File.Exists(tf))
        {
            bool inVs = false;
            foreach (var line in File.ReadLines(tf))
            {
                var s = line.Trim();
                if (s.StartsWith('[')) { inVs = s.Equals("[VisualStyles]", StringComparison.OrdinalIgnoreCase); continue; }
                if (inVs && s.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Environment.ExpandEnvironmentVariables(s[5..].Trim());
                    if (File.Exists(p)) return p;
                }
            }
        }
        return fallback;
    }

    /// <summary>Clones the current .theme (wallpaper, sounds, cursors are kept) but points [VisualStyles] at the new style.</summary>
    public static string BuildThemeText(string? currentTheme, string displayName, string stylePathToken)
    {
        var lines = (currentTheme != null && File.Exists(currentTheme)) ? File.ReadAllLines(currentTheme).ToList() : new List<string>();
        var result = new List<string>();
        string section = "";
        bool nameDone = false, pathDone = false, sawTheme = false, sawVs = false;
        foreach (var line in lines)
        {
            var s = line.Trim();
            if (s.StartsWith('['))
            {
                section = s;
                if (section.Equals("[Theme]", StringComparison.OrdinalIgnoreCase)) sawTheme = true;
                if (section.Equals("[VisualStyles]", StringComparison.OrdinalIgnoreCase)) sawVs = true;
                result.Add(line);
                if (section.Equals("[Theme]", StringComparison.OrdinalIgnoreCase)) { result.Add("DisplayName=" + displayName); nameDone = true; }
                continue;
            }
            if (s.StartsWith("ThemeId=", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Equals("[Theme]", StringComparison.OrdinalIgnoreCase) && s.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Equals("[VisualStyles]", StringComparison.OrdinalIgnoreCase) && s.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
            { result.Add("Path=" + stylePathToken); pathDone = true; continue; }
            result.Add(line);
        }
        if (!sawTheme) { result.Insert(0, "DisplayName=" + displayName); result.Insert(0, "[Theme]"); }
        if (!sawVs) { result.Add("[VisualStyles]"); result.Add("Path=" + stylePathToken); result.Add("ColorStyle=NormalColor"); result.Add("Size=NormalSize"); pathDone = true; }
        else if (!pathDone)
        {
            int idx = result.FindIndex(l => l.Trim().Equals("[VisualStyles]", StringComparison.OrdinalIgnoreCase));
            result.Insert(idx + 1, "Path=" + stylePathToken);
        }
        _ = nameDone;
        return string.Join("\r\n", result) + "\r\n";
    }
}

// ───────────────────────── Backups ─────────────────────────
class BackupInfo
{
    public string Dir = "", Reason = "", PrevTheme = "";
    public DateTime Created;
}

static class Backup
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinStyleStudio", "Backups");

    static readonly (string Key, string File)[] Keys =
    {
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes", "themes.reg"),
        (@"HKCU\Software\Microsoft\Windows\DWM",                   "dwm.reg"),
        (@"HKCU\Control Panel\Colors",                             "colors.reg"),
    };

    static void RunReg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("reg.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    public static string Create(string reason, string? sourceStyle = null)
    {
        var dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var theme = ThemeTools.CurrentThemeFile() ?? "";
        File.WriteAllLines(Path.Combine(dir, "meta.txt"), new[] { $"reason={reason}", $"currentTheme={theme}", $"source={sourceStyle}" });
        foreach (var (key, file) in Keys) RunReg($"export \"{key}\" \"{Path.Combine(dir, file)}\" /y");
        if (File.Exists(theme)) File.Copy(theme, Path.Combine(dir, "current.theme"), true);
        if (sourceStyle != null && File.Exists(sourceStyle)) File.Copy(sourceStyle, Path.Combine(dir, "source.msstyles"), true);
        return dir;
    }

    public static List<BackupInfo> List()
    {
        var list = new List<BackupInfo>();
        if (!Directory.Exists(Root)) return list;
        foreach (var d in Directory.GetDirectories(Root).OrderByDescending(x => x))
        {
            var meta = Path.Combine(d, "meta.txt");
            if (!File.Exists(meta)) continue;
            var kv = File.ReadAllLines(meta).Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
            list.Add(new BackupInfo
            {
                Dir = d,
                Created = Directory.GetCreationTime(d),
                Reason = kv.GetValueOrDefault("reason", ""),
                PrevTheme = kv.GetValueOrDefault("currentTheme", ""),
            });
        }
        return list;
    }

    public static string Restore(string dir)
    {
        Create("automatic safety backup before restore");
        foreach (var (_, file) in Keys)
        {
            var f = Path.Combine(dir, file);
            if (File.Exists(f)) RunReg($"import \"{f}\"");
        }
        Native.BroadcastThemeChange();

        var meta = File.ReadAllLines(Path.Combine(dir, "meta.txt")).Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
        var theme = meta.GetValueOrDefault("currentTheme", "");
        if (!File.Exists(theme)) theme = Path.Combine(dir, "current.theme");
        if (File.Exists(theme))
        {
            Process.Start(new ProcessStartInfo(theme) { UseShellExecute = true });
            return "Registry settings restored and your previous theme was re-applied.";
        }
        return "Registry settings restored. No previous .theme file was found - pick a theme in Settings > Personalization > Themes.";
    }
}

// ───────────────────────── Install & apply (the ONLY code path that touches Windows) ─────────────────────────
static class Installer
{
    public static void Run(IWin32Window owner, StyleFile style)
    {
        if (style.Pending.Count == 0)
        {
            MessageBox.Show(owner, "There are no staged edits to apply.", "Nothing to apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var raw = Prompt.Ask(owner, "Name your style", "Name for the new visual style (letters, digits, space, - and _):", "MyStyle");
        if (raw == null) return;
        var name = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        if (name.Length == 0) { MessageBox.Show(owner, "Invalid name."); return; }

        var confirm = MessageBox.Show(owner,
            $"Apply \"{name}\" to Windows?\n\n" +
            $"• {style.Pending.Count} edited image(s) will be written into a NEW style \"{name}\" under %SystemRoot%\\Resources\\Themes (UAC prompt).\n" +
            "• No existing Windows file is overwritten.\n" +
            "• A backup of your current theme and color settings is saved first, and can be restored from the Backups tab.\n" +
            "• Windows only loads unsigned styles if a patcher such as SecureUxTheme is installed; otherwise the theme will be rejected.\n\n" +
            "Do you want to continue?",
            "Confirm apply", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "WinStyleStudio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            var tmpStyle = Path.Combine(tmp, name + ".msstyles");
            style.Save(tmpStyle);

            var backupDir = Backup.Create($"before applying '{name}'", style.FilePath);

            var themeText = ThemeTools.BuildThemeText(ThemeTools.CurrentThemeFile(), name,
                $@"%SystemRoot%\Resources\Themes\{name}\{name}.msstyles");
            var tmpTheme = Path.Combine(tmp, name + ".theme");
            File.WriteAllText(tmpTheme, themeText, Encoding.Unicode);

            var script = Path.Combine(tmp, "install.cmd");
            File.WriteAllText(script,
                "@chcp 65001 >nul\r\n" +
                $"mkdir \"%SystemRoot%\\Resources\\Themes\\{name}\" 2>nul\r\n" +
                $"copy /y \"{tmpStyle}\" \"%SystemRoot%\\Resources\\Themes\\{name}\\{name}.msstyles\" || exit /b 1\r\n" +
                $"copy /y \"{tmpTheme}\" \"%SystemRoot%\\Resources\\Themes\\{name}.theme\" || exit /b 1\r\n",
                new UTF8Encoding(false));

            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
                   { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!)
            {
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException("The elevated install step failed (exit code " + p.ExitCode + ").");
            }

            var themePath = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "Resources", "Themes", name + ".theme");
            Process.Start(new ProcessStartInfo(themePath) { UseShellExecute = true });

            MessageBox.Show(owner,
                $"Style \"{name}\" installed and applied.\n\nBackup saved to:\n{backupDir}\n\nIf anything looks wrong, use the Backups tab > Restore.",
                "Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(owner, "Elevation was cancelled. Windows was not changed (a backup was still saved).", "Cancelled");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Apply failed: " + ex.Message + "\n\nWindows files were not overwritten. Use the Backups tab if the theme changed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
