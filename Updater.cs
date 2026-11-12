// Aggiornamento automatico da GitHub Releases.
// Una volta al giorno (se attivo) legge l'ultima release del repository; se è più recente scarica in background
// l'installer pubblicato lì e lo verifica con lo SHA-256 scritto nelle note della release ("SHA-256: <impronta>").
// L'installer verificato si avvia con un clic (pulsante sulla barra dei menu) oppure da solo, in silenzio, quando
// CorbolPad si chiude: alla riapertura c'è la versione nuova, con le stesse schede.
// Solo il CorbolPad installato (in %LOCALAPPDATA%\Programs\CorbolPad) si aggiorna da solo; una copia portatile lo propone e basta.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CorbolPad
{
    static class Updater
    {
        static int checking, downloading;
        public static string Version;           // versione disponibile, null se non ce n'è una più recente
        static string url, sha, page;
        public static string ReadyFile;         // installer scaricato e verificato, pronto da avviare
        public static event Action Changed;     // c'è una versione nuova o è pronta: aggiornare il menu

        // Indirizzi di GitHub (campi e non costanti solo per poterli provare con un server finto)
        static string ApiLatest = AppVersion.RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest";
        static string DownloadPrefix = AppVersion.RepoUrl + "/releases/download/";

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorbolPad", "update"); } }

        // Aggiornamenti automatici: attivi di default
        public static bool Auto
        {
            get { return Settings.Get("autoUpdate", true); }
            set { Settings.Set("autoUpdate", value); Settings.Save(); }
        }

        // CorbolPad installato con l'installer (non una copia portatile)
        public static bool Installed
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CorbolPad");
                try { return string.Equals(Path.GetDirectoryName(Application.ExecutablePath), dir, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        // All'avvio: toglie gli installer già usati e controlla se è passato un giorno dall'ultima volta
        public static void CheckIfDue(Control ui)
        {
            CleanUp();
            if (!Auto) return;
            DateTime last;
            if (DateTime.TryParse(Settings.Get("lastUpdCheck", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out last) &&
                DateTime.Now - last < TimeSpan.FromHours(24) && DateTime.Now >= last) return;
            Check(ui, false);
        }

        static void CleanUp()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (var f in Directory.GetFiles(Dir))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"^CorbolPad-Setup-(\d+\.\d+\.\d+)\.exe(\.part)?$");
                    System.Version v;
                    if (!m.Success || !System.Version.TryParse(m.Groups[1].Value, out v) || v <= new System.Version(AppVersion.Text) || m.Groups[2].Success)
                        try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public static void Check(Control ui, bool manual)
        {
            if (Interlocked.Exchange(ref checking, 1) == 1) return;
            Settings.Set("lastUpdCheck", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            Settings.Save();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, u = null, s = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var req = (HttpWebRequest)WebRequest.Create(ApiLatest);
                    req.Timeout = 15000;
                    req.UserAgent = "CorbolPad/" + AppVersion.Text;
                    req.Accept = "application/vnd.github+json";
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    // Solo un installer pubblicato nelle release di questo repository
                    m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(" + Regex.Escape(DownloadPrefix) + "[^\"]+\\.exe)\"");
                    if (m.Success) u = m.Groups[1].Value;
                    m = Regex.Match(json, "SHA-256:\\s*`?([0-9a-fA-F]{64})");
                    if (m.Success) s = m.Groups[1].Value.ToLowerInvariant();
                }
                catch { }
                checking = 0;
                try { ui.BeginInvoke(new Action(delegate { OnResult(ui, tag, u, s, manual); })); } catch { }
            });
        }

        static void OnResult(Control ui, string tag, string u, string s, bool manual)
        {
            System.Version latest;
            if (tag == null || !System.Version.TryParse(tag, out latest))
            {
                if (manual) Msg(ui, L.T("Impossibile controllare gli aggiornamenti. Verifica la connessione a Internet e riprova.",
                                        "Could not check for updates. Check your Internet connection and try again."), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest <= new System.Version(AppVersion.Text))
            {
                if (manual) Msg(ui, L.F("Hai già l'ultima versione di CorbolPad ({0}).", "You already have the latest version of CorbolPad ({0}).", AppVersion.Text),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Version = tag; url = u; sha = s;
            page = AppVersion.RepoUrl + "/releases/tag/v" + tag;
            Raise();
            // Senza installer o senza impronta da verificare non si scarica nulla da soli: resta la pagina della release
            if (url != null && sha != null) Download(ui, manual);
            else if (manual) Offer(ui);
        }

        static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        // Scarica e verifica l'installer; manual: alla fine propone subito di installarlo
        static void Download(Control ui, bool manual)
        {
            if (ReadyFile != null) { if (manual) Offer(ui); return; }
            if (Interlocked.Exchange(ref downloading, 1) == 1) return;
            string u = url, s = sha, ver = Version;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Path.Combine(Dir, "CorbolPad-Setup-" + ver + ".exe");
                string err = null;
                try
                {
                    Directory.CreateDirectory(Dir);
                    if (!(File.Exists(file) && HashOf(file) == s))
                    {
                        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                        string part = file + ".part";
                        using (var wc = new WebClient())
                        {
                            wc.Headers[HttpRequestHeader.UserAgent] = "CorbolPad/" + AppVersion.Text;
                            wc.DownloadFile(u, part);
                        }
                        if (HashOf(part) != s)
                        {
                            File.Delete(part);
                            throw new Exception(L.T("il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso).", "the downloaded file does not match the published one (SHA-256 mismatch)."));
                        }
                        if (File.Exists(file)) File.Delete(file);
                        File.Move(part, file);
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                downloading = 0;
                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        if (err != null)
                        {
                            if (manual && Msg(ui, L.F("Aggiornamento non riuscito: {0}\n\nAprire la pagina di download?", "Update failed: {0}\n\nOpen the download page?", err),
                                              MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                                Util.OpenUrl(page);
                            return;
                        }
                        ReadyFile = file;
                        Raise();
                        if (manual) Offer(ui);
                    }));
                }
                catch { }
            });
        }

        static string HashOf(string file)
        {
            using (var st = File.OpenRead(file))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(st)).Replace("-", "").ToLowerInvariant();
        }

        // Pulsante "Aggiorna" o voce del menu: installa adesso (l'installer chiude CorbolPad e lo riapre con le stesse schede)
        public static void Offer(Control ui)
        {
            if (Version == null) return;
            if (ReadyFile == null)
            {
                if (url != null && sha != null) { Download(ui, true); return; }
                Util.OpenUrl(page);
                return;
            }
            var r = Msg(ui, L.F("CorbolPad {0} è pronto da installare (ora hai la {1}).\n\nInstallarlo adesso? CorbolPad si chiude e si riapre con le stesse schede, anche quelle non salvate. " +
                                "Altrimenti si installa da solo quando chiudi CorbolPad.\n\nNovità: {2}",
                                "CorbolPad {0} is ready to install (you have {1}).\n\nInstall it now? CorbolPad will close and reopen with the same tabs, including unsaved ones. " +
                                "Otherwise it installs by itself when you close CorbolPad.\n\nWhat's new: {2}",
                                Version, AppVersion.Text, page), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;
            if (!Launch(false))
                Msg(ui, L.T("L'installer non si è avviato. Riprova o scaricalo dalla pagina della release.", "The installer did not start. Try again or download it from the release page."),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Alla chiusura di CorbolPad: se c'è un aggiornamento pronto, lo installa in silenzio (solo per il CorbolPad installato)
        public static void InstallOnExit()
        {
            if (ReadyFile == null || !Auto || !Installed) return;
            Launch(true);
        }

        static bool Launch(bool silent)
        {
            try
            {
                // Si ricontrolla l'impronta subito prima di avviarlo
                if (!File.Exists(ReadyFile) || HashOf(ReadyFile) != sha) { ReadyFile = null; return false; }
                Process.Start(new ProcessStartInfo(ReadyFile, silent ? "/silent" : "") { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });
                ReadyFile = null;
                return true;
            }
            catch { return false; }
        }

        static DialogResult Msg(Control owner, string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(owner, text, "CorbolPad", buttons, icon);
        }
    }
}
