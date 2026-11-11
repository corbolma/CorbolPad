// Assistente locale: llama-server (llama.cpp) in un processo separato, come il processo ":assistant" di ZioNVR.
// Se il modello va in crash o esaurisce la memoria, l'editor e i testi non salvati non ne risentono.
//   - Si avvia alla prima richiesta e si chiude dopo 5 minuti senza richieste (libera 2-4 GB di RAM).
//   - Ascolta solo su 127.0.0.1, su una porta libera scelta ogni volta, con una chiave casuale:
//     gli altri programmi del PC non possono usarlo.
//   - Un "job object" di Windows lo chiude anche se AppPad si chiude male.
//   - Due chiusure improvvise durante una risposta e l'assistente locale si spegne (si riattiva dalle impostazioni).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AppPad
{
    class LocalAi : IAiProvider
    {
        public static readonly LocalAi Instance = new LocalAi();

        const int ContextTokens = 16384;            // domanda + risposta; il testo mandato va limitato prima
        const int DefaultMaxTokens = 8192;
        const int StartTimeoutMs = 180000;          // caricamento del modello da disco lento
        public const int MaxCrashes = 2;
        public static int IdleMinutes = 5;

        readonly object gate = new object();
        Process proc;
        IntPtr job;
        int port;
        string key, modelPath, variant;
        volatile bool busy;
        DateTime lastUse;
        System.Threading.Timer idleTimer;
        readonly LinkedList<string> log = new LinkedList<string>();   // ultime righe di llama-server, per gli errori

        public string Name { get { return L.T("Assistente locale", "Local assistant"); } }
        public bool SendsTextOutside { get { return false; } }

        // Avvisi per l'interfaccia ("Avvio dell'assistente locale…", null = niente da dire); arrivano da altri thread
        public event Action<string> StatusChanged;

        public bool Running { get { lock (gate) return proc != null && !proc.HasExited; } }

        public static string LogPath { get { return Path.Combine(AiDownloader.Root, "llama-server.log"); } }

        // Chiusure improvvise durante una risposta, contate in settings.txt
        public static int Crashes
        {
            get { return Settings.Get("ai.local.crashes", 0); }
            set { Settings.Set("ai.local.crashes", value); Settings.Save(); }
        }

        // Il modello scelto nelle impostazioni: del catalogo (scaricato) o un .gguf dell'utente
        public static string ConfiguredModelPath()
        {
            string id = AiSettings.LocalModel;
            if (id.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return File.Exists(id) ? id : null;
            var m = AiCatalog.Find(id);
            return m != null ? AiDownloader.ModelPath(m) : null;
        }

        // Motivo per cui l'assistente locale non può partire, o null
        public static string NotReadyReason()
        {
            if (Crashes >= MaxCrashes)
                return L.F("Assistente locale spento: si è chiuso all'improvviso {0} volte. Riattivalo dalle impostazioni, magari con un modello più piccolo o senza GPU.",
                           "Local assistant turned off: it closed unexpectedly {0} times. Turn it back on in the settings, perhaps with a smaller model or without the GPU.", MaxCrashes);
            if (ConfiguredModelPath() == null || !AiDownloader.LlamaReady(AiSettings.LocalVariant))
                return L.T("L'assistente locale non è ancora scaricato: completa il download dalle impostazioni.",
                           "The local assistant is not downloaded yet: finish the download in the settings.");
            return null;
        }

        // ---- Richiesta ----

        public AiResult Run(AiRequest req, Action<string> onText, CancellationToken cancel)
        {
            string why = NotReadyReason();
            if (why != null) return new AiResult { Error = why };
            if (req.MaxTokens <= 0) req.MaxTokens = DefaultMaxTokens;

            OpenAiProvider p;
            try { p = EnsureRunning(cancel); }
            catch (OperationCanceledException) { return new AiResult { Cancelled = true }; }
            catch (Exception ex) { return new AiResult { Error = ex.Message }; }

            busy = true;
            lastUse = DateTime.Now;
            AiResult res;
            try { res = p.Run(req, onText, cancel); }
            finally { busy = false; lastUse = DateTime.Now; }

            // Il processo è morto mentre rispondeva: crash (memoria finita, driver della GPU...)
            if (!res.Cancelled && res.Error != null && !Running)
            {
                int n = Crashes + 1;
                Crashes = n;
                res.Error = L.T("L'assistente locale si è chiuso all'improvviso.", "The local assistant closed unexpectedly.")
                          + (n >= MaxCrashes ? "\n" + NotReadyReason() : "") + LastLog();
            }
            else if (res.Ok && Crashes > 0) Crashes = 0;   // le chiusure contano solo se consecutive
            return res;
        }

        OpenAiProvider Client()
        {
            return new OpenAiProvider
            {
                BaseUrl = "http://127.0.0.1:" + port + "/v1",
                ApiKey = key,
                Model = "local",
                DisplayName = Name,
                Local = true,
                TimeoutMs = 300000,   // su CPU lente la prima parola può tardare
                Extra = new Dictionary<string, object>
                {
                    // Qwen3.5 "pensa" prima di rispondere: per correggere o tradurre un testo non serve e rallenta
                    { "chat_template_kwargs", new Dictionary<string, object> { { "enable_thinking", false } } },
                    // Temperatura bassa e niente "presence penalty": l'editor chiede quasi sempre di riscrivere un testo
                    // fedelmente, e con i valori consigliati per la chat (0,7 e 1,5) il modello inventava parole ("belsole")
                    { "temperature", 0.2 }, { "top_p", 0.8 }, { "top_k", 20 }, { "min_p", 0.0 }, { "presence_penalty", 0.0 }
                }
            };
        }

        // ---- Avvio e arresto ----

        OpenAiProvider EnsureRunning(CancellationToken cancel, string forceVariant = null)
        {
            lock (gate)
            {
                string exe = AiDownloader.ServerExe(), model = ConfiguredModelPath(), v = forceVariant ?? AiSettings.LocalVariant;
                if (proc != null && !proc.HasExited && model == modelPath && v == variant) return Client();
                StopLocked();

                Status(L.T("Avvio dell'assistente locale…", "Starting the local assistant…"));
                port = FreePort();
                key = RandomKey();
                modelPath = model;
                variant = v;
                lock (log) log.Clear();

                var psi = new ProcessStartInfo(exe)
                {
                    Arguments = Args(model, v, port, key),
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => AddLog(e.Data);
                p.ErrorDataReceived += (s, e) => AddLog(e.Data);
                try { p.Start(); }
                catch (Exception ex) { throw new Exception(L.T("Impossibile avviare llama-server:", "Could not start llama-server:") + " " + ex.Message); }
                proc = p;
                AttachToJob(p);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // l'editor resta reattivo
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // Pronto quando /health risponde 200 (503 mentre carica il modello)
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (cancel.IsCancellationRequested) { StopLocked(); throw new OperationCanceledException(); }
                    if (p.HasExited)
                    {
                        proc = null;
                        throw new Exception(L.F("llama-server si è chiuso durante l'avvio (codice {0}).", "llama-server closed while starting (code {0}).", p.ExitCode) + LastLog());
                    }
                    if (Healthy()) break;
                    if (sw.ElapsedMilliseconds > StartTimeoutMs)
                    {
                        StopLocked();
                        throw new Exception(L.T("L'assistente locale non è partito in tempo.", "The local assistant did not start in time.") + LastLog());
                    }
                    cancel.WaitHandle.WaitOne(250);
                }
                lastUse = DateTime.Now;
                if (idleTimer == null) idleTimer = new System.Threading.Timer(delegate { StopIfIdle(); }, null, 30000, 30000);
                Status(null);
                return Client();
            }
        }

        static string Args(string model, string variant, int port, string key)
        {
            var a = new StringBuilder();
            a.Append("-m \"").Append(model).Append("\"");
            a.Append(" --host 127.0.0.1 --port ").Append(port).Append(" --api-key ").Append(key);
            a.Append(" -c ").Append(ContextTokens);
            a.Append(" --jinja");                                        // modello di chat del file .gguf (serve per enable_thinking)
            a.Append(" --reasoning off");                                // niente "ragionamento": per un editor rallenta e basta
            a.Append(" -np 1");                                          // una richiesta alla volta: tutto il contesto per lei
            a.Append(variant == "vulkan" ? " -ngl 999" : " -ngl 0");     // strati del modello sulla GPU
            int threads = Math.Max(1, Environment.ProcessorCount / 2);   // circa i core fisici; il resto all'editor
            a.Append(" -t ").Append(threads);
            return a.ToString();
        }

        bool Healthy()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                req.Timeout = 2000;
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        // ---- Prova di velocità ----

        // Velocità di scrittura (token al secondo) con il modello scelto, sul processore o sulla GPU: una risposta
        // breve a temperatura 0, misurata da llama-server stesso (esclusi l'avvio e la lettura della domanda).
        // Serve il pacchetto llama.cpp per GPU, che fa girare anche la prova sul processore. Bloccante.
        public double MeasureSpeed(bool gpu, CancellationToken cancel)
        {
            if (ConfiguredModelPath() == null || !AiDownloader.LlamaReady("vulkan"))
                throw new Exception(NotReadyReason() ?? L.T("Manca il motore per GPU.", "The GPU engine is missing."));
            OpenAiProvider p;
            lock (gate)
            {
                p = EnsureRunning(cancel, gpu ? "vulkan" : "cpu");
                busy = true;
            }
            try
            {
                // Prima una risposta brevissima per scaldare il motore (la prima è sempre più lenta), poi quella misurata
                Generate(p, 8, cancel);
                return Generate(p, 160, cancel);
            }
            finally
            {
                busy = false;
                Stop();     // la prossima richiesta riparte con la variante delle impostazioni
            }
        }

        double Generate(OpenAiProvider p, int maxTokens, CancellationToken cancel)
        {
            {
                var body = new Dictionary<string, object>
                {
                    { "model", "local" },
                    { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", "Write a paragraph of about 120 words about lighthouses." } } } },
                    { "max_tokens", maxTokens }, { "temperature", 0.0 }, { "stream", false },
                    { "chat_template_kwargs", new Dictionary<string, object> { { "enable_thinking", false } } }
                };
                var req = (HttpWebRequest)WebRequest.Create(p.BaseUrl + "/chat/completions");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
                req.Proxy = null;
                req.Timeout = req.ReadWriteTimeout = 300000;
                byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                using (cancel.Register(req.Abort))
                {
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                        object t;
                        var timings = o != null && o.TryGetValue("timings", out t) ? t as Dictionary<string, object> : null;
                        if (timings != null && timings.TryGetValue("predicted_per_second", out t))
                            return Convert.ToDouble(t, System.Globalization.CultureInfo.InvariantCulture);
                        throw new Exception(L.T("llama-server non ha indicato la velocità.", "llama-server did not report the speed."));
                    }
                }
            }
        }

        // Chiude llama-server (per esempio prima di cancellare i file o cambiare modello)
        public void Stop()
        {
            lock (gate) StopLocked();
        }

        void StopLocked()
        {
            var p = proc;
            proc = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
            try { p.Dispose(); } catch { }
            Status(null);
        }

        void StopIfIdle()
        {
            if (busy || !Monitor.TryEnter(gate)) return;
            try
            {
                if (proc != null && !busy && DateTime.Now - lastUse > TimeSpan.FromMinutes(IdleMinutes)) StopLocked();
            }
            finally { Monitor.Exit(gate); }
        }

        void Status(string s)
        {
            var h = StatusChanged;
            if (h != null) try { h(s); } catch { }
        }

        // ---- Registro di llama-server ----

        void AddLog(string line)
        {
            if (line == null) return;
            lock (log)
            {
                log.AddLast(line);
                while (log.Count > 200) log.RemoveFirst();
            }
        }

        // Ultime righe utili del registro, da aggiungere a un messaggio di errore; le salva anche su file
        string LastLog()
        {
            string[] lines;
            lock (log) { lines = new string[log.Count]; log.CopyTo(lines, 0); }
            try { Directory.CreateDirectory(AiDownloader.Root); File.WriteAllLines(LogPath, lines, new UTF8Encoding(false)); } catch { }
            var tail = new List<string>();
            for (int i = lines.Length - 1; i >= 0 && tail.Count < 4; i--)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || tail.Count == 0)
                    tail.Insert(0, l.Length > 200 ? l.Substring(0, 200) + "…" : l);
            }
            return tail.Count == 0 ? "" : "\n\n" + string.Join("\n", tail);
        }

        // ---- Utilità ----

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string RandomKey()
        {
            var b = new byte[24];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        // Job object con "chiudi i processi quando si chiude il job": l'handle lo tiene AppPad, quindi
        // se AppPad termina (anche in crash) Windows chiude llama-server
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        void AttachToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }
    }
}
