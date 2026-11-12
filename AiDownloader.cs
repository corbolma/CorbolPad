// Assistente locale: scarica llama-server (llama.cpp, licenza MIT) e il modello scelto, solo quando l'utente
// attiva l'assistente locale. Come in ZioNVR: ogni file si controlla con il suo SHA-256, un download interrotto
// riprende da dove era arrivato, si lascia sempre spazio libero sul disco, e i file che non servono più si cancellano.
// Tutto sta in %LOCALAPPDATA%\CorbolPad\ai (non in Roaming: sono GB che non vanno sincronizzati).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CorbolPad
{
    class AiModel
    {
        public string Id, Name, FileName, Url, Sha256, License;
        public long Size, MinRam;
    }

    // Modelli proposti per l'assistente locale. Solo licenze Apache 2.0 o MIT, scaricabili senza account.
    // Qwen3.5 (Alibaba) nella quantizzazione Q4_K_M di unsloth; l'URL punta a un commit preciso del repository,
    // così il file non può cambiare sotto i piedi e lo SHA-256 resta valido.
    static class AiCatalog
    {
        public const string Light = "qwen3.5-2b";
        public const string Better = "qwen3.5-4b";

        public static readonly AiModel[] Models =
        {
            new AiModel
            {
                Id = Light, Name = "Qwen3.5 2B", License = "Apache 2.0",
                FileName = "Qwen3.5-2B-Q4_K_M.gguf",
                Url = "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf",
                Size = 1280835840,
                Sha256 = "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
                MinRam = 8L << 30
            },
            new AiModel
            {
                Id = Better, Name = "Qwen3.5 4B", License = "Apache 2.0",
                FileName = "Qwen3.5-4B-Q4_K_M.gguf",
                Url = "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf",
                Size = 2740937888,
                Sha256 = "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
                MinRam = 16L << 30
            }
        };

        public static AiModel Find(string id)
        {
            return Models.FirstOrDefault(m => m.Id == id);
        }

        // Il modello consigliato per questo PC: il migliore che la RAM regge
        public static AiModel Recommended()
        {
            long ram = TotalRam();
            return Models.LastOrDefault(m => ram <= 0 || ram >= m.MinRam * 15 / 16) ?? Models[0];
        }

        // Motivo per cui questo PC non è adatto al modello, o null. La RAM "da 16 GB" vista da Windows è un po' meno.
        public static string Unsuitable(AiModel m)
        {
            long ram = TotalRam();
            if (ram > 0 && ram < m.MinRam * 15 / 16)
                return L.F("RAM insufficiente: servono almeno {0} GB", "Not enough RAM: at least {0} GB needed", m.MinRam >> 30);
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static long TotalRam()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
    }

    // Stato da mostrare nelle impostazioni
    class AiDownloadStatus
    {
        public string Text;          // cosa sta facendo, già tradotto
        public long Done, Total;     // byte, per la barra di avanzamento (Total = 0: indeterminato)
        public bool Finished;        // il lavoro è terminato (bene se Error == null)
        public string Error;         // motivo dell'errore, già tradotto
    }

    class AiDownloader
    {
        // Versione di llama.cpp provata con CorbolPad, fissata con dimensione e SHA-256 dei pacchetti (presi dalla
        // pagina della release su GitHub). Un aggiornamento di llama.cpp non cambia nulla finché non si cambia qui.
        // Per aggiornare: https://github.com/ggml-org/llama.cpp/releases/tag/<tag> → asset "llama-<tag>-bin-win-<cpu|vulkan>-x64.zip".
        public const string LlamaTag = "b12110";
        class LlamaPackage { public string Variant; public long Size; public string Sha256; }
        static readonly LlamaPackage[] LlamaPackages =
        {
            new LlamaPackage { Variant = "cpu",    Size = 24708445, Sha256 = "98901e327c252875accaf2d1bf364e10e868843b0b63468baad1aa945cdc5da2" },
            new LlamaPackage { Variant = "vulkan", Size = 37661146, Sha256 = "9732a5e48887ca54d0bfe4af4324dc85fd56cd8cda19b52df3542fa6ebe6874f" }
        };

        const long MinFree = 2L << 30;     // spazio da lasciare libero sul disco dopo i download
        const int Retries = 5;             // tentativi per file, ognuno riprende da dove era arrivato

        public static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorbolPad", "ai"); }
        }
        public static string ModelsDir { get { return Path.Combine(Root, "models"); } }
        public static string LlamaDir { get { return Path.Combine(Root, "llama"); } }

        static string UserAgent { get { return "CorbolPad/" + AppVersion.Text; } }

        // Chiamato dal thread del download: chi aggiorna l'interfaccia deve usare BeginInvoke
        public event Action<AiDownloadStatus> Progress;

        Thread thread;
        volatile bool cancel;
        public bool Busy { get { return thread != null && thread.IsAlive; } }

        // ---- Cosa c'è già sul disco ----

        // Percorso del modello se scaricato e verificato, altrimenti null
        public static string ModelPath(AiModel m)
        {
            string f = Path.Combine(ModelsDir, m.FileName);
            try
            {
                if (new FileInfo(f).Length == m.Size && File.ReadAllText(f + ".sha256").Trim() == m.Sha256) return f;
            }
            catch { }
            return null;
        }

        // Percorso di llama-server.exe se installato e verificato, altrimenti null
        public static string ServerExe()
        {
            foreach (var d in InstalledLlamaDirs())
            {
                string exe = FindServer(d);
                if (exe != null) return exe;
            }
            return null;
        }

        // Cartelle llama\<tag>-<variante> complete, la più recente per prima
        static IEnumerable<string> InstalledLlamaDirs()
        {
            if (!Directory.Exists(LlamaDir)) return new string[0];
            return Directory.GetDirectories(LlamaDir)
                .Where(d => File.Exists(Path.Combine(d, "ok.txt")))
                .OrderByDescending(d => TagNumber(Path.GetFileName(d)));
        }

        static string FindServer(string dir)
        {
            try { return Directory.GetFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { return null; }
        }

        static int TagNumber(string name)
        {
            var m = Regex.Match(name, "^b(\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // llama.cpp installato, della versione giusta e adatto alla variante: il pacchetto per GPU (Vulkan) contiene
        // anche il motore per processore, quindi va bene pure per "cpu" (con -ngl 0)
        public static bool LlamaReady(string variant)
        {
            if (ServerExe() == null || InstalledTag() != LlamaTag) return false;
            string v = InstalledVariant();
            return v == variant || (variant == "cpu" && v == "vulkan");
        }

        // Variante installata ("cpu" o "vulkan"), o null
        public static string InstalledVariant()
        {
            string d = InstalledLlamaDirs().FirstOrDefault();
            if (d == null) return null;
            string n = Path.GetFileName(d);
            int i = n.IndexOf('-');
            return i > 0 ? n.Substring(i + 1) : null;
        }

        // Versione installata di llama.cpp (es. "b12110"), o null
        public static string InstalledTag()
        {
            string d = InstalledLlamaDirs().FirstOrDefault();
            if (d == null) return null;
            string n = Path.GetFileName(d);
            int i = n.IndexOf('-');
            return i > 0 ? n.Substring(0, i) : null;
        }

        // Dimensione del pacchetto di llama.cpp da scaricare per la variante ("cpu" o "vulkan")
        public static long LlamaSize(string variant)
        {
            var p = LlamaPackages.FirstOrDefault(x => x.Variant == variant);
            return p != null ? p.Size : 0;
        }

        // Spazio occupato dall'assistente locale
        public static long UsedBytes()
        {
            try
            {
                if (!Directory.Exists(Root)) return 0;
                return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { return 0; }
        }

        // ---- Pulizia ----

        // Assistente locale spento: via tutto (GB di file). llama-server deve essere già chiuso.
        public static void RemoveAll()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }

        // Cancella i modelli del catalogo diversi da quello scelto (e i download a metà di quelli)
        public static void RemoveOtherModels(AiModel keep)
        {
            if (!Directory.Exists(ModelsDir)) return;
            foreach (var f in Directory.GetFiles(ModelsDir))
            {
                string n = Path.GetFileName(f);
                if (keep != null && n.StartsWith(keep.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }

        // ---- Download ----

        // Scarica in background quel che manca: llama-server nella variante indicata ("cpu" o "vulkan") e,
        // se model != null, il modello. Se è già tutto a posto termina subito.
        public void Start(AiModel model, string variant)
        {
            if (Busy) return;
            cancel = false;
            thread = new Thread(delegate () { Run(model, variant); });
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Name = "CorbolPad AI download";
            thread.Start();
        }

        // Interrompe: il file a metà resta, il prossimo Start riprende da lì
        public void Cancel() { cancel = true; }

        void Report(string text, long done, long total)
        {
            var h = Progress;
            if (h != null) h(new AiDownloadStatus { Text = text, Done = done, Total = total });
        }

        void Run(AiModel model, string variant)
        {
            string error = null;
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                if (variant != "vulkan") variant = "cpu";
                if (model != null)
                {
                    string why = AiCatalog.Unsuitable(model);
                    if (why != null) throw new AiDownloadException(why);
                    RemoveOtherModels(model);
                }
                // Prima il modello (HuggingFace), poi llama.cpp (GitHub): se uno dei due siti non risponde,
                // intanto si porta a casa l'altro, che è il file più grande
                if (model != null && ModelPath(model) == null) DownloadModel(model);
                if (!LlamaReady(variant)) InstallLlama(variant);
            }
            catch (OperationCanceledException) { error = L.T("Scaricamento interrotto", "Download stopped"); }
            catch (AiDownloadException ex) { error = ex.Message; }
            catch (Exception ex) { error = ex.Message; }

            var h = Progress;
            if (h != null) h(new AiDownloadStatus
            {
                Finished = true, Error = error,
                Text = error ?? L.T("Assistente locale pronto", "Local assistant ready")
            });
        }

        void DownloadModel(AiModel m)
        {
            Directory.CreateDirectory(ModelsDir);
            string final = Path.Combine(ModelsDir, m.FileName);
            string part = final + ".part";
            try { File.Delete(final + ".sha256"); } catch { }
            if (File.Exists(final) && !File.Exists(part)) File.Move(final, part); // file presente ma non verificato: si ricontrolla
            string label = L.F("Scaricamento di {0}", "Downloading {0}", m.Name);
            Download(m.Url, part, m.Size, label);
            Verify(part, m.Sha256, m.Name);
            if (File.Exists(final)) File.Delete(final);
            File.Move(part, final);
            File.WriteAllText(final + ".sha256", m.Sha256);
        }

        // llama-server per Windows x64 dalla release ufficiale di llama.cpp indicata in LlamaTag
        void InstallLlama(string variant)
        {
            var pkg = LlamaPackages.First(p => p.Variant == variant);
            string tag = LlamaTag;
            string assetName = "llama-" + tag + "-bin-win-" + variant + "-x64.zip";
            string url = "https://github.com/ggml-org/llama.cpp/releases/download/" + tag + "/" + assetName;
            long size = pkg.Size;
            string sha = pkg.Sha256;

            Directory.CreateDirectory(LlamaDir);
            string zip = Path.Combine(LlamaDir, assetName + ".part");
            // Download a metà di un'altra release: non serve più
            foreach (var f in Directory.GetFiles(LlamaDir, "*.part"))
                if (!f.Equals(zip, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }

            Download(url, zip, size, L.F("Scaricamento di llama.cpp {0}", "Downloading llama.cpp {0}", tag));
            Verify(zip, sha, "llama.cpp");

            Report(L.T("Estrazione di llama.cpp…", "Extracting llama.cpp…"), 0, 0);
            string dir = Path.Combine(LlamaDir, tag + "-" + variant);
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            if (FindServer(tmp) == null) throw new AiDownloadException(L.T("llama-server.exe non trovato nel pacchetto di llama.cpp", "llama-server.exe not found in the llama.cpp package"));
            File.WriteAllText(Path.Combine(tmp, "ok.txt"), assetName + "\r\nsha256:" + sha + "\r\n");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            File.Delete(zip);

            // Le versioni precedenti non servono più (se una è ancora in uso resta lì fino alla prossima volta)
            foreach (var d in Directory.GetDirectories(LlamaDir))
                if (!d.Equals(dir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(d, true); } catch { }
        }

        // Scarica url in part, riprendendo da quanto c'è già. Controlla prima lo spazio libero.
        void Download(string url, string part, long size, string label)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > size) { File.Delete(part); have = 0; }
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(part))).AvailableFreeSpace;
            if (free < size - have + MinFree)
                throw new AiDownloadException(L.F("Spazio insufficiente: servono {0} liberi sul disco",
                                                  "Not enough space: {0} free needed on the disk", Util.FormatBytes(size - have + MinFree)));

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    DownloadOnce(url, part, size, label);
                    return;
                }
                catch (Exception ex)
                {
                    if (cancel || ex is AiDownloadException || attempt >= Retries) throw;
                    if (!(ex is WebException || ex is IOException)) throw;
                    Report(label + " — " + L.T("nuovo tentativo…", "retrying…"), File.Exists(part) ? new FileInfo(part).Length : 0, size);
                    for (int i = 0; i < 50 && !cancel; i++) Thread.Sleep(100);
                    if (cancel) throw new OperationCanceledException();
                }
            }
        }

        void DownloadOnce(string url, string part, long size, string label)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have == size) return;

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 60000;   // GitHub a volte impiega decine di secondi a rispondere
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true; // HuggingFace e GitHub rimandano a una CDN
            if (have > 0) req.AddRange(have);

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                // 200 invece di 206: il server ricomincia da capo
                bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (!append) have = 0;
                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    var buf = new byte[1 << 20];
                    int lastTick = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancel) throw new OperationCanceledException();
                        if (have + n > size) throw new AiDownloadException(L.T("Il file scaricato è più grande del previsto", "The downloaded file is larger than expected"));
                        dst.Write(buf, 0, n);
                        have += n;
                        if (Environment.TickCount - lastTick > 250)
                        {
                            lastTick = Environment.TickCount;
                            Report(label, have, size);
                        }
                    }
                }
            }
            if (have != size) throw new IOException(L.T("Scaricamento incompleto", "Incomplete download"));
            Report(label, have, size);
        }

        // Confronta lo SHA-256 del file; se non torna lo cancella, così il prossimo tentativo riparte da zero
        void Verify(string file, string sha, string what)
        {
            long total = new FileInfo(file).Length, done = 0;
            string label = L.F("Verifica di {0}…", "Verifying {0}…", what);
            Report(label, 0, total);
            SHA256 h;
            try { h = new SHA256Cng(); } catch { h = SHA256.Create(); }
            using (h)
            using (var st = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var buf = new byte[1 << 20];
                int n, lastTick = 0;
                while ((n = st.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel) throw new OperationCanceledException();
                    h.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (Environment.TickCount - lastTick > 250) { lastTick = Environment.TickCount; Report(label, done, total); }
                }
                h.TransformFinalBlock(buf, 0, 0);
                string got = BitConverter.ToString(h.Hash).Replace("-", "").ToLowerInvariant();
                if (got != sha)
                {
                    st.Close();
                    try { File.Delete(file); } catch { }
                    throw new AiDownloadException(L.F("{0}: il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso)",
                                                      "{0}: the downloaded file does not match the published one (SHA-256 mismatch)", what));
                }
            }
        }
    }

    // Errore con un messaggio già pronto per l'utente: niente nuovi tentativi
    class AiDownloadException : Exception
    {
        public AiDownloadException(string msg) : base(msg) { }
    }
}
