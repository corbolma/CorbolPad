// Installer di CorbolPad: un solo exe che contiene CorbolPad.exe.
// Installa per l'utente corrente (nessun permesso di amministratore) in %LOCALAPPDATA%\Programs\CorbolPad.
//   CorbolPad-Setup.exe              -> finestra di installazione / aggiornamento
//   CorbolPad-Setup.exe /silent      -> installazione silenziosa (mantiene le scelte di un'installazione precedente)
//   Uninstall.exe /uninstall      -> disinstallazione (registrata in Impostazioni > App)
//   Uninstall.exe /uninstall /silent [/purge] -> senza domande; /purge cancella anche impostazioni, sessione e assistente
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Installazione CorbolPad")]

namespace CorbolPad.Setup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Func<string, bool> has = a => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            bool silent = has("/silent") || has("/s");
            L.LoadFromSettings(); // stessa lingua scelta nell'app, altrimenti quella di Windows
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (has("/uninstall")) return Installer.Uninstall(silent, has("/purge")) ? 0 : 1;
                if (silent)
                {
                    // In silenzioso non si torna mai a una versione più vecchia, salvo /force.
                    var inst = Installer.InstalledVersion();
                    if (inst != null && inst > Installer.Current && !has("/force")) return 3;
                    Installer.Install(Installer.DesktopShortcutExists(), Installer.ContextMenuEnabled(), Installer.FileTypesEnabled(), false);
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(L.T("Errore", "Error") + ": " + ex.Message, "CorbolPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
    }

    static class Installer
    {
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CorbolPad";
        const string ProgId = "AppPad.Document"; // come prima del cambio di nome: resta valida l'app predefinita scelta dall'utente

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CorbolPad");
        public static readonly string ExePath = Path.Combine(InstallDir, "CorbolPad.exe");
        public static readonly string UninstallerPath = Path.Combine(InstallDir, "Uninstall.exe");
        static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CorbolPad.lnk");
        static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CorbolPad.lnk");

        // Dati dell'app: impostazioni (roaming) e sessione, testo non salvato, Scintilla, assistente locale
        public static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolPad");
        public static readonly string LocalDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorbolPad");

        // Estensioni per cui CorbolPad compare in "Apri con" e in Impostazioni > App predefinite
        static readonly string[] TextTypes =
        {
            ".txt", ".log", ".md", ".markdown", ".ini", ".cfg", ".conf", ".config", ".properties", ".toml", ".env",
            ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".reg", ".inf", ".nfo", ".diz",
            ".bat", ".cmd", ".ps1", ".psm1", ".sh", ".sql", ".cs", ".java", ".js", ".ts", ".py", ".php", ".rb", ".go", ".rs",
            ".c", ".h", ".cpp", ".hpp", ".css", ".scss", ".html", ".htm", ".svg", ".gitignore", ".gitattributes", ".editorconfig", ".srt"
        };

        public static readonly Version Current = Version.Parse(AppVersion.Text);

        // Versione realmente installata: letta dall'exe (non solo dal registro), null se non installata.
        public static Version InstalledVersion()
        {
            if (!File.Exists(ExePath)) return OldApp.InstalledVersion(); // installato col nome vecchio, o null
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(ExePath);
                if (fv.FileMajorPart + fv.FileMinorPart + fv.FileBuildPart > 0)
                    return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { }
            using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                Version v;
                string s = k != null ? k.GetValue("DisplayVersion") as string : null;
                return s != null && Version.TryParse(s, out v) ? v : new Version(0, 0, 0);
            }
        }

        public static string V(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        public static bool DesktopShortcutExists() { return File.Exists(DesktopLnk) || (!File.Exists(ExePath) && File.Exists(OldApp.DesktopLnk)); }

        // Scelte salvate nella chiave di disinstallazione: attive di default, in un aggiornamento si mantengono
        static bool Choice(string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                if (k == null) { var old = OldApp.Choice(name); return old == null || old.Value != 0; } // anche dal nome vecchio
                return !(k.GetValue(name) is int) || (int)k.GetValue(name) != 0;
            }
        }
        public static bool ContextMenuEnabled() { return Choice("ContextMenu"); }
        public static bool FileTypesEnabled() { return Choice("FileTypes"); }

        // ---------- Esplora file (solo per l'utente corrente, nessun amministratore) ----------

        const string ContextKey = @"Software\Classes\*\shell\CorbolPad";            // tasto destro su qualunque file
        const string AppKey = @"Software\Classes\Applications\CorbolPad.exe";       // "Apri con"
        const string CapKey = @"Software\CorbolPad\Capabilities";                   // Impostazioni > App predefinite
        const string RegAppsKey = @"Software\RegisteredApplications";

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
        static void NotifyShell() { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } // SHCNE_ASSOCCHANGED

        static string OpenCommand { get { return "\"" + ExePath + "\" \"%1\""; } }

        static void RegisterContextMenu()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(ContextKey))
            {
                k.SetValue("", L.T("Apri con CorbolPad", "Open with CorbolPad"));
                k.SetValue("Icon", "\"" + ExePath + "\",0");
                using (var c = k.CreateSubKey("command")) c.SetValue("", OpenCommand);
            }
        }

        static void UnregisterContextMenu()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ContextKey, false); } catch { }
        }

        static void RegisterFileTypes()
        {
            // Il tipo di documento di CorbolPad
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", L.T("Documento di testo", "Text document"));
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", "\"" + ExePath + "\",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", OpenCommand);
            }
            // "Apri con": CorbolPad tra i programmi proposti per queste estensioni (non cambia il programma predefinito)
            using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                k.SetValue("FriendlyAppName", "CorbolPad");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", OpenCommand);
                using (var t = k.CreateSubKey("SupportedTypes"))
                    foreach (var ext in TextTypes) t.SetValue(ext, "");
            }
            foreach (var ext in TextTypes)
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids"))
                    k.SetValue(ProgId, new byte[0], RegistryValueKind.None);
            // Impostazioni > App > App predefinite > CorbolPad
            using (var k = Registry.CurrentUser.CreateSubKey(CapKey))
            {
                k.SetValue("ApplicationName", "CorbolPad");
                k.SetValue("ApplicationDescription", L.T("Blocco note a schede, veloce anche con file di grandi dimensioni", "Tabbed notepad, fast even with large files"));
                k.SetValue("ApplicationIcon", "\"" + ExePath + "\",0");
                using (var f = k.CreateSubKey("FileAssociations"))
                    foreach (var ext in TextTypes) f.SetValue(ext, ProgId);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(RegAppsKey))
                k.SetValue("CorbolPad", CapKey);
        }

        static void UnregisterFileTypes()
        {
            foreach (var ext in TextTypes)
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", true))
                        if (k != null) k.DeleteValue(ProgId, false);
                }
                catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(AppKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\CorbolPad", false); } catch { }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegAppsKey, true))
                    if (k != null) k.DeleteValue("CorbolPad", false);
            }
            catch { }
        }

        // Chiude CorbolPad se è aperta: prima con garbo (chiusura della finestra: la sessione e il testo non salvato
        // vengono conservati, senza domande), poi forzando. Con CorbolPad si chiude anche l'assistente locale.
        public static void CloseRunning()
        {
            OldApp.CloseRunning(null); // la versione col nome vecchio (salva la sessione chiudendosi)
            int session = Process.GetCurrentProcess().SessionId;
            Func<Process[]> mine = () => Process.GetProcessesByName("CorbolPad").Where(p => p.SessionId == session).ToArray();
            if (mine().Length > 0)
            {
                foreach (var p in mine())
                    try { p.CloseMainWindow(); } catch { }
                var until = DateTime.Now.AddSeconds(10);
                while (mine().Length > 0 && DateTime.Now < until) Thread.Sleep(200);
                foreach (var p in mine())
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            // llama-server dell'assistente locale rimasto in vita (normalmente si chiude con CorbolPad)
            string ai = Path.Combine(LocalDataDir, "ai"), oldAi = Path.Combine(OldApp.LocalDir, "ai");
            foreach (var p in Process.GetProcessesByName("llama-server").Where(p => p.SessionId == session))
                try
                {
                    string exe = p.MainModule.FileName;
                    if (exe.StartsWith(ai, StringComparison.OrdinalIgnoreCase) || exe.StartsWith(oldAi, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
        }

        public static bool IsRunning()
        {
            int session = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName("CorbolPad").Concat(Process.GetProcessesByName(OldApp.Name)).Any(p => p.SessionId == session);
        }

        public static void Install(bool desktop, bool contextMenu, bool fileTypes, bool launch)
        {
            CloseRunning();
            // Dal nome vecchio: dati nelle cartelle nuove (sessione e testo non salvato compresi), vecchia installazione
            // tolta (le scelte sono già state lette)
            OldApp.MoveData();
            OldApp.RemoveInstall();
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\" + OldApp.Name, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + OldApp.Name, false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(RegAppsKey, true)) if (k != null) k.DeleteValue(OldApp.Name, false); } catch { }
            Directory.CreateDirectory(InstallDir);

            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("CorbolPad.payload"))
            {
                if (src == null) throw new InvalidOperationException("pacchetto CorbolPad.exe mancante nell'installer");
                string tmp = ExePath + ".new";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(tmp, ExePath);
            }

            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallerPath, true);

            string desc = L.T("Blocco note a schede", "Tabbed notepad");
            CreateShortcut(StartMenuLnk, ExePath, "", desc);
            if (desktop) CreateShortcut(DesktopLnk, ExePath, "", desc);
            else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);
            if (contextMenu) RegisterContextMenu(); else UnregisterContextMenu();
            if (fileTypes) RegisterFileTypes(); else UnregisterFileTypes();
            NotifyShell();

            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", "CorbolPad");
                k.SetValue("DisplayVersion", AppVersion.Text);
                k.SetValue("VersionMajor", Current.Major, RegistryValueKind.DWord);
                k.SetValue("VersionMinor", Current.Minor, RegistryValueKind.DWord);
                k.SetValue("Publisher", AppVersion.Author);
                k.SetValue("URLInfoAbout", AppVersion.RepoUrl);
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallerPath + "\" /uninstall /silent");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024 + new FileInfo(UninstallerPath).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                // Scelte ricordate negli aggiornamenti
                k.SetValue("ContextMenu", contextMenu ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("FileTypes", fileTypes ? 1 : 0, RegistryValueKind.DWord);
            }

            UpdateOldAppKey(TextTypes);
            RetargetPins();
            if (launch) Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = InstallDir });
        }

        static long DirSize(string dir)
        {
            try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
            catch { return 0; }
        }

        static string FormatBytes(long b)
        {
            if (b < 1024 * 1024) return Math.Max(1, b / 1024) + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return (b / 1073741824.0).ToString("0.00") + " GB";
        }

        public static bool Uninstall(bool silent, bool purge)
        {
            string title = L.T("Disinstalla CorbolPad", "Uninstall CorbolPad");
            if (!silent)
            {
                if (MessageBox.Show(L.T("Vuoi disinstallare CorbolPad?", "Do you want to uninstall CorbolPad?"), title,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return false;
                long data = DirSize(SettingsDir) + DirSize(LocalDataDir);
                if (data > 0)
                {
                    // I dati possono contenere testo non salvato: di default si tengono
                    var r = MessageBox.Show(
                        L.F("Eliminare anche i dati di CorbolPad ({0})?\n\n" +
                            "Sono le impostazioni, le schede aperte con il testo non salvato e i file dell'assistente locale. " +
                            "Se li tieni, reinstallando CorbolPad ritrovi tutto com'era.",
                            "Also delete CorbolPad's data ({0})?\n\n" +
                            "These are the settings, the open tabs with unsaved text and the local assistant files. " +
                            "If you keep them, reinstalling CorbolPad brings everything back.", FormatBytes(data)),
                        title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (r == DialogResult.Cancel) return false;
                    purge = r == DialogResult.Yes;
                }
            }

            CloseRunning();
            foreach (var f in new[] { StartMenuLnk, DesktopLnk })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            UnregisterContextMenu();
            UnregisterFileTypes();
            NotifyShell();
            if (purge)
                foreach (var d in new[] { SettingsDir, LocalDataDir })
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            try { if (File.Exists(ExePath)) File.Delete(ExePath); } catch { }

            // L'uninstaller gira dalla cartella da eliminare: la cancella un cmd dopo la nostra uscita.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });

            if (!silent) MessageBox.Show(L.T("CorbolPad è stato disinstallato.", "CorbolPad has been uninstalled."), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // "Apri con" col nome vecchio: se è l'app predefinita scelta dall'utente per qualche tipo di file
        // (Apri con > Usa sempre questa app), la voce resta e apre il nuovo exe, altrimenti si toglie
        static void UpdateOldAppKey(string[] exts)
        {
            string key = @"Software\Classes\Applications\" + OldApp.Name + ".exe";
            string id = @"Applications\" + OldApp.Name + ".exe";
            bool chosen = false;
            foreach (var e in exts)
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + e + @"\UserChoice"))
                        if (k != null && string.Equals(k.GetValue("ProgId") as string, id, StringComparison.OrdinalIgnoreCase)) chosen = true;
                }
                catch { }
            if (!chosen)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
                return;
            }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("FriendlyAppName", "CorbolPad");
                    try { k.DeleteSubKeyTree("SupportedTypes", false); } catch { } // niente doppioni in "Apri con"
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + ExePath + "\" \"%1\"");
                }
            }
            catch { }
        }

        // Icone fissate alla barra delle applicazioni che aprono l'exe col nome vecchio: ora aprono quello nuovo
        // (stesso AppUserModelID, quindi l'icona fissata resta quella della finestra aperta)
        static void RetargetPins()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(dir)) return;
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
            {
                IShellLinkW link = null;
                try
                {
                    link = (IShellLinkW)new CShellLink();
                    var pf = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                    pf.Load(lnk, 2 /* STGM_READWRITE */);
                    var sb = new System.Text.StringBuilder(1024);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                    if (string.Equals(sb.ToString(), OldApp.ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        link.SetPath(ExePath);
                        link.SetWorkingDirectory(InstallDir);
                        link.SetIconLocation(ExePath, 0);
                        pf.Save(lnk, true);
                    }
                }
                catch { }
                finally { if (link != null) Marshal.FinalReleaseComObject(link); }
            }
        }

        // Deve coincidere con Program.AppUserModelId in CorbolPad.cs
        const string AppUserModelId = "AppPad.Editor"; // come prima del cambio di nome: le icone già fissate alla barra restano della stessa app

        // Crea il collegamento con l'AppUserModelID, così l'icona fissata alla barra corrisponde alla finestra aperta.
        static void CreateShortcut(string lnkPath, string target, string args, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                if (args.Length > 0) link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(description);
                link.SetIconLocation(target, 0);

                var store = (IPropertyStore)link;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(AppUserModelId) };
                try { store.SetValue(ref key, ref pv); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(pv.p); }

                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }
    }

    class SetupForm : Form
    {
        readonly CheckBox cbContext, cbTypes, cbDesktop, cbLaunch;
        readonly Button btnInstall, btnCancel;
        readonly Label lblStatus;

        public SetupForm()
        {
            var installed = Installer.InstalledVersion();
            var cur = Installer.Current;
            string vi = installed != null ? Installer.V(installed) : null, vc = Installer.V(cur);
            string infoText, action;
            bool warn = false;
            if (installed != null && !File.Exists(Installer.ExePath))
            {
                action = L.T("Aggiorna", "Update");
                infoText = L.F("{0} {1} diventa CorbolPad {2}: cambia solo il nome, le impostazioni vengono mantenute.",
                               "{0} {1} becomes CorbolPad {2}: only the name changes, your settings are kept.", OldApp.Name, vi, vc);
            }
            else if (installed == null)
            {
                action = L.T("Installa", "Install");
                infoText = L.T("CorbolPad verrà installato solo per il tuo utente, senza permessi di amministratore, nella cartella:",
                               "CorbolPad will be installed for your user only, without administrator rights, in the folder:");
            }
            else if (installed < cur)
            {
                action = L.T("Aggiorna", "Update");
                infoText = L.F("Aggiornamento dalla versione {0} alla {1}. Impostazioni e schede aperte vengono mantenute.",
                               "Update from version {0} to {1}. Your settings and open tabs will be kept.", vi, vc);
            }
            else if (installed == cur)
            {
                action = L.T("Reinstalla", "Reinstall");
                infoText = L.F("La versione {0} è già installata. Puoi reinstallarla (impostazioni e schede aperte vengono mantenute).",
                               "Version {0} is already installed. You can reinstall it (your settings and open tabs will be kept).", vc);
            }
            else
            {
                action = L.T("Installa comunque", "Install anyway");
                warn = true;
                infoText = L.F("Attenzione: è installata una versione più recente ({0}). Questo installer contiene la {1}, più vecchia. Continuando la sostituirai.",
                               "Warning: a newer version ({0}) is installed. This installer contains the older {1}. Continuing will replace it.", vi, vc);
            }
            if (Installer.IsRunning())
                infoText += "\n" + L.T("CorbolPad è aperto: verrà chiuso e riaperto con le stesse schede, anche quelle non salvate.",
                                       "CorbolPad is open: it will be closed and reopened with the same tabs, including unsaved ones.");

            var accent = Color.FromArgb(37, 99, 235);
            Text = action + " CorbolPad";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f); // layout pensato a 96 DPI, scalato in base allo schermo
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(470, 300);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = accent };
            var pic = new PictureBox { Location = new Point(20, 15), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { using (var ic = new Icon(Icon, 64, 64)) pic.Image = ic.ToBitmap(); } catch { }
            var title = new Label
            {
                Text = "CorbolPad " + AppVersion.Text, ForeColor = Color.White, AutoSize = true,
                Font = new Font("Segoe UI Semibold", 15f), Location = new Point(88, 16), BackColor = Color.Transparent
            };
            var sub = new Label
            {
                Text = L.T("Blocco note a schede, veloce anche con file di grandi dimensioni, che riapre le schede dove le avevi lasciate. Con un assistente facoltativo.",
                           "Tabbed notepad, fast even with large files, that reopens your tabs where you left them. With an optional assistant."),
                ForeColor = Color.FromArgb(219, 234, 254),
                AutoSize = true, MaximumSize = new Size(370, 0), Location = new Point(90, 50), BackColor = Color.Transparent
            };
            header.Controls.AddRange(new Control[] { pic, title, sub });

            var info = new Label
            {
                Location = new Point(20, 100), AutoSize = true, MaximumSize = new Size(430, 0),
                Text = infoText, ForeColor = warn ? Color.FromArgb(190, 60, 0) : SystemColors.ControlText
            };
            var path = new Label
            {
                Location = new Point(20, 136), Size = new Size(430, 18), ForeColor = Color.DimGray,
                Text = Installer.InstallDir, AutoEllipsis = true
            };

            cbContext = new CheckBox
            {
                Text = L.T("Aggiungi \"Apri con CorbolPad\" al tasto destro dei file", "Add \"Open with CorbolPad\" to the right-click menu of files"),
                AutoSize = true, Checked = Installer.ContextMenuEnabled()
            };
            cbTypes = new CheckBox
            {
                Text = L.T("Proponi CorbolPad per aprire i file di testo (\"Apri con\" e App predefinite)", "Offer CorbolPad to open text files (\"Open with\" and Default apps)"),
                AutoSize = true, Checked = Installer.FileTypesEnabled()
            };
            cbDesktop = new CheckBox { Text = L.T("Crea un collegamento sul desktop", "Create a desktop shortcut"), AutoSize = true, Checked = Installer.DesktopShortcutExists() };
            cbLaunch = new CheckBox { Text = L.T("Avvia CorbolPad al termine", "Launch CorbolPad when finished"), AutoSize = true, Checked = true };
            foreach (var cb in new[] { cbContext, cbTypes, cbDesktop, cbLaunch }) cb.Left = 22;

            lblStatus = new Label { Location = new Point(20, 262), Size = new Size(240, 20), ForeColor = Color.DimGray };
            btnInstall = new Button { Text = action, Location = new Point(240, 256), MinimumSize = new Size(120, 30), AutoSize = true };
            btnCancel = new Button { Text = L.T("Annulla", "Cancel"), Location = new Point(366, 256), MinimumSize = new Size(90, 30), AutoSize = true, DialogResult = DialogResult.Cancel };
            btnInstall.Click += delegate { DoInstall(); };
            // DialogResult da solo non chiude: l'installer è la finestra principale, non un dialogo
            btnCancel.Click += delegate { Close(); };
            AcceptButton = btnInstall;
            CancelButton = btnCancel;

            // Piè di pagina: autore, licenza e link al progetto
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(244, 246, 249) };
            string owner = "© 2026 " + AppVersion.Author + "  ·  ";
            string lic = L.T("Licenza MIT", "MIT License");
            var credits = new LinkLabel
            {
                Text = owner + lic, AutoSize = true, Location = new Point(18, 7), ForeColor = Color.DimGray,
                LinkColor = accent, ActiveLinkColor = accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline, BackColor = Color.Transparent
            };
            credits.LinkArea = new LinkArea(owner.Length, lic.Length);
            credits.LinkClicked += delegate { OpenUrl(AppVersion.RepoUrl + "/blob/main/LICENSE"); };
            var site = new LinkLabel
            {
                Text = "GitHub", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right,
                LinkColor = accent, ActiveLinkColor = accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline, BackColor = Color.Transparent
            };
            site.LinkClicked += delegate { OpenUrl(AppVersion.RepoUrl); };
            footer.Controls.AddRange(new Control[] { credits, site });

            Controls.AddRange(new Control[] { header, info, path, cbContext, cbTypes, cbDesktop, cbLaunch, lblStatus, btnInstall, btnCancel, footer });

            // Dispone i controlli sotto il testo informativo, qualunque sia la sua altezza (righe, DPI)
            Load += delegate
            {
                header.Height = Math.Max(header.Height, sub.Bottom + 12);
                info.Top = header.Bottom + 14;
                int y = info.Bottom + 6;
                path.Top = y; y = path.Bottom + 12;
                foreach (var cb in new[] { cbContext, cbTypes, cbDesktop, cbLaunch }) { cb.Top = y; y = cb.Bottom + 4; }
                y += 14;
                btnInstall.Top = btnCancel.Top = y;
                // Pulsanti allineati a destra: si allargano se la traduzione è lunga
                btnCancel.Left = ClientSize.Width - 14 - btnCancel.Width;
                btnInstall.Left = btnCancel.Left - 6 - btnInstall.Width;
                lblStatus.Width = Math.Max(60, btnInstall.Left - 26);
                lblStatus.Top = y + 6;
                ClientSize = new Size(ClientSize.Width, btnInstall.Bottom + 14 + footer.Height);
                site.Location = new Point(footer.ClientSize.Width - 18 - site.Width, credits.Top);
            };
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void DoInstall()
        {
            btnInstall.Enabled = btnCancel.Enabled = false;
            lblStatus.Text = L.T("Installazione in corso...", "Installing...");
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                bool wasRunning = Installer.IsRunning();
                Installer.Install(cbDesktop.Checked, cbContext.Checked, cbTypes.Checked, cbLaunch.Checked || wasRunning);
                Cursor = Cursors.Default;
                string done = L.T("CorbolPad è stato installato. Lo trovi nel menu Start.", "CorbolPad has been installed. You can find it in the Start menu.");
                if (cbContext.Checked)
                    done += "\n\n" + L.T("Per aprire un file: tasto destro → Apri con CorbolPad (in Windows 11: Mostra altre opzioni).",
                                         "To open a file: right-click → Open with CorbolPad (in Windows 11: Show more options).");
                if (cbTypes.Checked)
                    done += "\n\n" + L.T("Per usarlo sempre con i file .txt: tasto destro su un file .txt → Apri con → Scegli un'altra app → CorbolPad → Sempre. " +
                                         "Oppure da Impostazioni → App → App predefinite → CorbolPad.",
                                         "To always use it for .txt files: right-click a .txt file → Open with → Choose another app → CorbolPad → Always. " +
                                         "Or from Settings → Apps → Default apps → CorbolPad.");
                done += "\n\n" + L.T("Per disinstallarlo: Impostazioni → App → App installate → CorbolPad.", "To uninstall it: Settings → Apps → Installed apps → CorbolPad.");
                MessageBox.Show(this, done, "CorbolPad", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                lblStatus.Text = "";
                btnInstall.Enabled = btnCancel.Enabled = true;
                MessageBox.Show(this, L.T("Installazione non riuscita:", "Installation failed:") + "\n" + ex.Message, "CorbolPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
