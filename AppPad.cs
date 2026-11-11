// AppPad - blocco note a schede, veloce anche con file di centinaia di MB, con la sessione ripristinata all'avvio.
// L'editor è Scintilla (lo stesso di Notepad++). Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("AppPad")]

namespace AppPad
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        public const string AppUserModelId = "AppPad.Editor";

        // AppPad.exe [file...] -> apre i file; se AppPad è già aperto li passa alla finestra esistente
        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(L.T("Si è verificato un errore:", "An error occurred:") + "\n" + e.Exception.Message, "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Settings.Load();
            L.Set(Settings.Get("lang", "auto"));

            var files = new List<string>();
            foreach (var a in args)
            {
                string p = a.Trim().Trim('"');
                if (p.Length == 0 || p.StartsWith("/")) continue;
                try { files.Add(Path.GetFullPath(p)); } catch { }
            }

            bool primary;
            var mutex = new Mutex(true, "AppPad.Instance." + Environment.UserName, out primary);
            if (!primary && SingleInstance.Send(files)) return;

            try { SciLib.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show(L.T("Impossibile avviare l'editor (Scintilla.dll):", "Could not start the editor (Scintilla.dll):") + "\n" + ex.Message,
                    "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(new MainForm(files, primary));
            GC.KeepAlive(mutex);
        }
    }

    // Una sola finestra: le istanze successive passano i file a quella già aperta tramite una named pipe.
    static class SingleInstance
    {
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        static string PipeName { get { return "AppPad-" + Environment.UserName + "-" + Process.GetCurrentProcess().SessionId; } }

        public static bool Send(List<string> files)
        {
            try
            {
                AllowSetForegroundWindow(-1);
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(3000);
                    using (var w = new StreamWriter(c, new UTF8Encoding(false)))
                    {
                        w.WriteLine("open");
                        foreach (var f in files) w.WriteLine(f);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        public static void Listen(Action<List<string>> onOpen)
        {
            var t = new Thread(delegate ()
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            var lines = new List<string>();
                            using (var r = new StreamReader(s, Encoding.UTF8))
                            {
                                string ln;
                                while ((ln = r.ReadLine()) != null) lines.Add(ln);
                            }
                            if (lines.Count > 0 && lines[0] == "open") onOpen(lines.Skip(1).Where(x => x.Length > 0).ToList());
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    static class Util
    {
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static void SelectInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString(b < 10 * 1024 ? "0.0" : "0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return (b / 1073741824.0).ToString("0.00") + " GB";
        }

        public static string N(long v) { return v.ToString("N0", CultureInfo.CurrentCulture); }

        public static Icon AppIcon()
        {
            try
            {
                using (var st = typeof(Util).Assembly.GetManifestResourceStream("AppPad.apppad.ico"))
                    if (st != null) return new Icon(st);
            }
            catch { }
            return null;
        }
    }

    // Impostazioni in %APPDATA%\AppPad\settings.txt, righe "chiave=valore".
    static class Settings
    {
        static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppPad", "settings.txt");
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path_)) return;
                foreach (var ln in File.ReadAllLines(Path_, Encoding.UTF8))
                {
                    int i = ln.IndexOf('=');
                    if (i > 0) values[ln.Substring(0, i).Trim()] = ln.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_));
                File.WriteAllLines(Path_, values.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string Get(string key, string def) { string v; return values.TryGetValue(key, out v) ? v : def; }
        public static int Get(string key, int def) { int v; return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public static float Get(string key, float def) { float v; return float.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def; }
        public static bool Get(string key, bool def) { string v = Get(key, ""); return v == "1" ? true : v == "0" ? false : def; }
        public static void Set(string key, string v) { values[key] = v ?? ""; }
        public static void Set(string key, int v) { values[key] = v.ToString(CultureInfo.InvariantCulture); }
        public static void Set(string key, float v) { values[key] = v.ToString(CultureInfo.InvariantCulture); }
        public static void Set(string key, bool v) { values[key] = v ? "1" : "0"; }
        public static void Remove(string key) { values.Remove(key); }
    }

    // Colori di menu e barra di stato secondo il tema (chiaro/scuro)
    class ThemedRenderer : ToolStripProfessionalRenderer
    {
        readonly Theme t;

        class Colors : ProfessionalColorTable
        {
            readonly Theme t;
            public Colors(Theme t) { this.t = t; UseSystemColors = false; }
            public override Color MenuItemSelected { get { return t.Hover; } }
            public override Color MenuItemBorder { get { return t.Hover; } }
            public override Color MenuBorder { get { return t.Border; } }
            public override Color MenuItemSelectedGradientBegin { get { return t.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return t.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return t.Pressed; } }
            public override Color MenuItemPressedGradientMiddle { get { return t.Pressed; } }
            public override Color MenuItemPressedGradientEnd { get { return t.Pressed; } }
            public override Color ToolStripDropDownBackground { get { return t.Chrome; } }
            public override Color ImageMarginGradientBegin { get { return t.Chrome; } }
            public override Color ImageMarginGradientMiddle { get { return t.Chrome; } }
            public override Color ImageMarginGradientEnd { get { return t.Chrome; } }
            public override Color MenuStripGradientBegin { get { return t.Chrome; } }
            public override Color MenuStripGradientEnd { get { return t.Chrome; } }
            public override Color StatusStripGradientBegin { get { return t.Chrome; } }
            public override Color StatusStripGradientEnd { get { return t.Chrome; } }
            public override Color ToolStripBorder { get { return t.Chrome; } }
            public override Color SeparatorDark { get { return t.Border; } }
            public override Color SeparatorLight { get { return t.Chrome; } }
            public override Color CheckBackground { get { return t.Pressed; } }
            public override Color CheckSelectedBackground { get { return t.Pressed; } }
            public override Color CheckPressedBackground { get { return t.Pressed; } }
            public override Color ButtonSelectedHighlight { get { return t.Hover; } }
            public override Color ButtonSelectedGradientBegin { get { return t.Hover; } }
            public override Color ButtonSelectedGradientMiddle { get { return t.Hover; } }
            public override Color ButtonSelectedGradientEnd { get { return t.Hover; } }
            public override Color ButtonPressedGradientBegin { get { return t.Pressed; } }
            public override Color ButtonPressedGradientMiddle { get { return t.Pressed; } }
            public override Color ButtonPressedGradientEnd { get { return t.Pressed; } }
            public override Color ButtonSelectedBorder { get { return t.Hover; } }
        }

        public ThemedRenderer(Theme t) : base(new Colors(t)) { this.t = t; RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var c = e.Item.Enabled ? t.ChromeText : t.ChromeTextDim;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = t.ChromeTextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            r.Inflate(2, 2);
            using (var b = new SolidBrush(t.Pressed)) e.Graphics.FillRectangle(b, r);
            var g = e.Graphics;
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float s = r.Height / 16f;
            using (var p = new Pen(t.ChromeText, 1.6f * s))
                g.DrawLines(p, new[] { new PointF(r.X + 4 * s, r.Y + 8.5f * s), new PointF(r.X + 7 * s, r.Y + 11.5f * s), new PointF(r.X + 12.5f * s, r.Y + 5 * s) });
            g.SmoothingMode = old;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
        }
    }

    // Piccola finestra per chiedere un valore (Vai a riga)
    class InputForm : Form
    {
        readonly TextBox box;

        public static string Ask(IWin32Window owner, string title, string label, string value, int width = 300)
        {
            using (var f = new InputForm(title, label, value, width))
                return f.ShowDialog(owner) == DialogResult.OK ? f.box.Text : null;
        }

        InputForm(string title, string label, string value, int width)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(width, 110);
            var lbl = new Label { Text = label, AutoSize = true, MaximumSize = new Size(width - 24, 0), Location = new Point(12, 12) };
            box = new TextBox { Text = value, Location = new Point(12, 34), Width = width - 24 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(width - 178, 70), Size = new Size(80, 28) };
            var cancel = new Button { Text = L.T("Annulla", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(width - 92, 70), Size = new Size(80, 28) };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { lbl, box, ok, cancel });
            // L'etichetta può andare a capo: il resto si dispone sotto (a questo punto le misure sono già scalate per i DPI)
            Load += delegate
            {
                box.Top = lbl.Bottom + lbl.Margin.Bottom + 6;
                ok.Top = cancel.Top = box.Bottom + 14;
                ClientSize = new Size(ClientSize.Width, ok.Bottom + 12);
            };
            Shown += delegate { box.SelectAll(); box.Focus(); };
        }
    }

    class AboutForm : Form
    {
        public static void ShowFor(IWin32Window owner, Icon icon)
        {
            using (var f = new AboutForm(icon)) f.ShowDialog(owner);
        }

        AboutForm(Icon icon)
        {
            Text = L.T("Informazioni su AppPad", "About AppPad");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            ClientSize = new Size(400, 220);
            if (icon != null) Icon = icon;

            var accent = Color.FromArgb(37, 99, 235);
            var pic = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            if (icon != null) try { pic.Image = new Icon(icon, 64, 64).ToBitmap(); } catch { }
            var title = new Label { Text = "AppPad " + AppVersion.Text, AutoSize = true, Location = new Point(82, 18), Font = new Font("Segoe UI Semibold", 14f) };
            var desc = new Label
            {
                Text = L.T("Blocco note a schede, veloce anche con file di grandi dimensioni",
                           "Tabbed notepad, fast even with large files"),
                AutoSize = true, MaximumSize = new Size(300, 0), Location = new Point(84, 50), ForeColor = Color.DimGray
            };
            var author = new Label { Text = "© 2026 " + AppVersion.Author, AutoSize = true, Location = new Point(84, 90) };
            string sci = L.T("Editor: Scintilla di Neil Hodgson", "Editor: Scintilla by Neil Hodgson");
            var links = new LinkLabel
            {
                Text = sci, AutoSize = true, Location = new Point(84, 112),
                LinkColor = accent, ActiveLinkColor = accent, LinkBehavior = LinkBehavior.HoverUnderline
            };
            links.Links.Clear();
            links.Links.Add(sci.IndexOf("Scintilla"), 9, "https://www.scintilla.org/License.txt");
            links.LinkClicked += (s, e) => Util.OpenUrl((string)e.Link.LinkData);

            var ok = new Button { Text = "OK", Size = new Size(90, 30), DialogResult = DialogResult.OK };
            AcceptButton = CancelButton = ok;
            Controls.AddRange(new Control[] { pic, title, desc, author, links, ok });

            Load += delegate
            {
                author.Top = desc.Bottom + 14;
                links.Top = author.Bottom + 4;
                ok.Location = new Point(ClientSize.Width - 16 - ok.Width, links.Bottom + 16);
                ClientSize = new Size(ClientSize.Width, ok.Bottom + 14);
            };
        }
    }
}
