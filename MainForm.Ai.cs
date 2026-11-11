// Finestra principale: l'assistente nell'editor. Il risultato compare mentre viene scritto, al posto della
// selezione (un solo Ctrl+Z riporta il testo originale) o in una scheda nuova. Durante la scrittura la scheda
// è in sola lettura; Esc ferma e rimette il testo com'era.
// Testi lunghi: si lavora a parti. Correggere/tradurre/riformulare sostituiscono una parte alla volta (Esc lascia
// le parti finite); riassunto e spiegazione prendono appunti su ogni parte e scrivono il risultato dagli appunti.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppPad
{
    partial class MainForm
    {
        const int LocalMaxChars = 12000;        // l'assistente locale ha 16.384 token per domanda e risposta insieme
        const int CloudMaxChars = 400000;
        const int LocalPartChars = 4000;        // parti da trasformare: la risposta è lunga quanto la parte
        const int CloudPartChars = 20000;
        const int MaxLongChars = 2000000;       // oltre, anche a parti non ha senso

        readonly ToolStripStatusLabel sAi = new ToolStripStatusLabel();
        readonly System.Windows.Forms.Timer aiTimer = new System.Windows.Forms.Timer { Interval = 80 };
        AiJob aiJob;

        class AiJob
        {
            public Doc Doc;
            public SciEditor Ed;
            public AiAction Action;
            public AiOutput Output;
            public IAiProvider Provider;
            public long Start, Pos;             // inizio del testo generato e punto in cui si scrive (byte)
            public bool Changed;                // il documento è stato modificato (c'è qualcosa da annullare)
            public bool WasReadOnly;
            public bool SeenText;               // arrivato il primo carattere non vuoto
            public bool Detached;               // chiuso in anticipo (scheda o finestra chiusa)
            public string Eol;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public readonly StringBuilder Pending = new StringBuilder();

            // Lavoro a parti
            public List<long> PartLen;          // sostituzione: lunghezza in byte di ogni parte del testo originale
            public List<string> PartText;       // scheda nuova: il testo di ogni parte, letto all'inizio
            public int Parts, Done;             // parti in tutto e parti finite
            public long PartStart, BodyStart;   // la parte in corso: dove comincia e dove comincia la risposta
            public long DoneEnd;                // fine dell'ultima parte finita
            public string PartOriginal, Trail;  // testo originale della parte in corso e spazi finali da rimettere
            public string Held = "";            // spazi finali della risposta, scritti solo se segue altro testo
        }

        // ---- Menu ----

        ToolStripMenuItem BuildAiMenu(bool context)
        {
            var m = new ToolStripMenuItem(context ? L.T("Assistente", "Assistant") : L.T("&Assistente", "&Assistant"));
            foreach (var a in AiAction.All)
            {
                if (a == null) { m.DropDownItems.Add(new ToolStripSeparator()); continue; }
                var action = a;
                // Le scorciatoie si registrano solo sul menu principale
                var mi = Mi(a.Label(), context ? Keys.None : a.Keys, delegate { RunAi(action); },
                            a.KeysText);
                mi.Enabled = aiJob == null;
                m.DropDownItems.Add(mi);
            }
            m.DropDownItems.Add(new ToolStripSeparator());
            var settings = Mi(L.T("&Impostazioni dell'assistente…", "Assistant settin&gs…"), Keys.None, ShowAiSettings);
            settings.Enabled = aiJob == null;
            m.DropDownItems.Add(settings);
            var stop = Mi(L.T("Fer&ma", "S&top"), Keys.None, StopAi, "Esc");
            stop.Enabled = aiJob != null;
            m.DropDownItems.Add(stop);
            if (!context)
                m.DropDownOpening += delegate
                {
                    foreach (ToolStripItem it in m.DropDownItems)
                        if (it is ToolStripMenuItem) it.Enabled = it == stop ? aiJob != null : aiJob == null;
                };
            return m;
        }

        // ---- Avvio ----

        // Il provider scelto, o null con il motivo
        IAiProvider AiProvider(out string why)
        {
            why = null;
            if (!AiSettings.Enabled)
            {
                why = L.T("L'assistente è spento. Attivalo in Assistente > Impostazioni dell'assistente.",
                          "The assistant is turned off. Turn it on in Assistant > Assistant settings.");
                return null;
            }
            if (AiSettings.Provider == AiSettings.Local)
            {
                why = LocalAi.NotReadyReason();
                return why == null ? LocalAi.Instance : null;
            }
            var p = AiSettings.CloudProvider();
            if (p == null)
                why = L.T("Il servizio dell'assistente non è configurato (manca la chiave API o il modello). Completa le impostazioni dell'assistente.",
                          "The assistant service is not set up (API key or model missing). Complete the assistant settings.");
            return p;
        }

        void ShowAiSettings()
        {
            if (aiJob != null) return;
            AiSettingsForm.ShowFor(this);
            if (aiPanel.Visible) ShowAiPanel();
        }

        void AiMessage(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(this, text, L.T("Assistente", "Assistant"), MessageBoxButtons.OK, icon);
        }

        void RunAi(AiAction a)
        {
            // "Chiedi" apre il pannello della conversazione
            if (a.Id == "ask") { ShowAiPanel(); return; }
            if (aiJob != null) return;
            if (aiChat != null)
            {
                AiMessage(L.T("L'assistente sta già rispondendo nel pannello: attendi o fermalo.", "The assistant is already answering in the panel: wait or stop it."), MessageBoxIcon.Information);
                return;
            }
            var d = active;
            var ed = Ed;
            if (ed == null) return;

            string why;
            var provider = AiProvider(out why);
            if (provider == null)
            {
                if (MessageBox.Show(this, why + "\n\n" + L.T("Aprire le impostazioni dell'assistente?", "Open the assistant settings?"),
                                    L.T("Assistente", "Assistant"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    ShowAiSettings();
                return;
            }

            if (ed.Send(Sci.SCI_GETSELECTIONS) > 1)
            {
                AiMessage(L.T("L'assistente lavora su una sola selezione alla volta.", "The assistant works on one selection at a time."), MessageBoxIcon.Information);
                return;
            }

            // Il testo: la selezione; senza selezione tutto il documento
            long s = ed.SelectionStart, e = ed.SelectionEnd;
            if (s == e) { s = 0; e = ed.Length; }
            if (s == e)
            {
                AiMessage(L.T("Non c'è testo su cui lavorare.", "There is no text to work on."), MessageBoxIcon.Information);
                return;
            }
            if (e - s > (long)MaxLongChars * 4 || ed.Send(Sci.SCI_COUNTCHARACTERS, s, e) > MaxLongChars)
            {
                AiMessage(L.F("Il testo è troppo lungo per l'assistente, anche diviso in parti: al massimo {0} caratteri. Seleziona una parte del testo.",
                              "The text is too long for the assistant, even split into parts: {0} characters at most. Select part of the text.", Util.N(MaxLongChars)),
                          MessageBoxIcon.Information);
                return;
            }

            // Le parti: da trasformare, piccole (la risposta è lunga quanto il testo); da leggere, grandi quanto il modello permette
            bool replace = a.Output != AiOutput.NewTab;
            bool cloud = provider.SendsTextOutside;
            var parts = SplitParts(ed, s, e, replace ? (cloud ? CloudPartChars : LocalPartChars) : (cloud ? CloudMaxChars : LocalMaxChars));
            if (parts.Count > 1 && !ConfirmLong(provider, replace, ed.Send(Sci.SCI_COUNTCHARACTERS, s, e), parts.Count)) return;
            if (cloud && !ConfirmCloud(provider)) return;

            var job = new AiJob { Action = a, Provider = provider, Output = a.Output, Parts = parts.Count };
            if (!replace)
            {
                job.PartText = new List<string>();
                long p = s;
                foreach (long len in parts) { job.PartText.Add(ed.GetTextRange(p, p + len)); p += len; }
                NewDoc();
                d = active;
                ed = Ed;
                if (ed == null) return;
                job.Start = job.Pos = 0;
            }
            else
            {
                job.PartLen = parts;
                ed.Send(Sci.SCI_BEGINUNDOACTION);
                job.Start = job.Pos = job.DoneEnd = s;
                ed.Send(Sci.SCI_GOTOPOS, s);
            }
            job.Doc = d;
            job.Ed = ed;
            job.Eol = d.Eol == Sci.SC_EOL_CRLF ? "\r\n" : d.Eol == Sci.SC_EOL_CR ? "\r" : "\n";
            job.WasReadOnly = ed.ReadOnly;
            ed.ReadOnly = true;
            aiJob = job;
            ShowAiStatus(L.F("{0} sta scrivendo… Esc per fermare", "{0} is writing… Press Esc to stop", provider.Name));

            var local = provider as LocalAi;
            Action<string> onStatus = st => { if (st != null) try { BeginInvoke(new Action(delegate { if (aiJob == job) ShowAiStatus(st + " " + L.T("Esc per fermare", "Esc to stop")); })); } catch { } };
            if (local != null) local.StatusChanged += onStatus;

            var t = new Thread(delegate ()
            {
                AiResult res;
                Action<string> onText = chunk => { if (chunk != null) lock (job.Pending) job.Pending.Append(chunk); };
                try { res = replace ? RunReplaceParts(job, onText) : RunReadParts(job, onText); }
                catch (Exception ex) { res = new AiResult { Error = ex.Message }; }
                if (local != null) local.StatusChanged -= onStatus;
                try { BeginInvoke(new Action(delegate { FinishAi(job, res); })); } catch { }
            });
            t.IsBackground = true;
            t.Name = "AppPad AI";
            t.Start();
            aiTimer.Start();
        }

        // Prima volta con un servizio cloud: il testo esce dal PC, meglio dirlo
        bool ConfirmCloud(IAiProvider p)
        {
            string key = "ai.cloudOk." + p.Name.ToLowerInvariant();
            if (Settings.Get(key, false)) return true;
            var r = MessageBox.Show(this,
                L.F("Il testo su cui lavora l'assistente (la selezione o il documento, e le domande che scrivi) verrà inviato a {0} per essere elaborato.\n\nContinuare? Non verrà più chiesto per {0}.",
                    "The text the assistant works on (the selection or the document, and the questions you type) will be sent to {0} for processing.\n\nContinue? You won't be asked again for {0}.", p.Name),
                L.T("Assistente", "Assistant"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r != DialogResult.OK) return false;
            Settings.Set(key, true);
            Settings.Save();
            return true;
        }

        // Testo lungo: si dice che andrà a parti (e quanto può durare) prima di cominciare
        bool ConfirmLong(IAiProvider p, bool replace, long chars, int parts)
        {
            string msg = replace
                ? L.F("Il testo è lungo ({0} caratteri): {1} lo elaborerà in {2} parti, una dopo l'altra, e ogni parte prende il posto di quella originale appena è pronta.\n\nCon Esc puoi fermarlo quando vuoi: le parti finite restano, il resto rimane com'era. Ctrl+Z riporta tutto il testo originale.",
                      "The text is long ({0} characters): {1} will process it in {2} parts, one after the other, and each part replaces the original as soon as it is ready.\n\nPress Esc to stop at any time: the finished parts stay, the rest is left as it was. Ctrl+Z brings back the whole original text.",
                      Util.N(chars), p.Name, parts)
                : L.F("Il testo è lungo ({0} caratteri): {1} lo leggerà in {2} parti prendendo appunti su ognuna, poi scriverà il risultato dagli appunti. Con un testo così lungo il risultato è meno preciso.",
                      "The text is long ({0} characters): {1} will read it in {2} parts taking notes on each one, then write the result from the notes. With such a long text the result is less precise.",
                      Util.N(chars), p.Name, parts);
            if (!p.SendsTextOutside)
                msg += "\n\n" + L.T("Con l'assistente locale ogni parte può richiedere qualche minuto.", "With the local assistant each part may take a few minutes.");
            return MessageBox.Show(this, msg + "\n\n" + L.T("Continuare?", "Continue?"), L.T("Assistente", "Assistant"),
                                   MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        }

        // ---- Divisione in parti ----

        // Lunghezze in byte delle parti di [s, e), ognuna di al massimo max caratteri. Si taglia di preferenza a una riga
        // vuota (fine di un paragrafo), poi a un a capo, poi dopo una frase, poi a uno spazio; mai prima di metà parte.
        static List<long> SplitParts(SciEditor ed, long s, long e, int max)
        {
            var parts = new List<long>();
            long p = s;
            while (p < e)
            {
                long q = ed.Send(Sci.SCI_POSITIONRELATIVE, p, max);       // 0 = oltre la fine del documento
                if (q <= p || q >= e) { parts.Add(e - p); break; }
                long min = ed.Send(Sci.SCI_POSITIONRELATIVE, p, max / 2);
                if (min <= p) min = p + 1;
                long b = ParagraphBreak(ed, min, q);
                if (b < 0)
                {
                    long ls = ed.Send(Sci.SCI_POSITIONFROMLINE, ed.LineFromPosition(q));
                    if (ls > min) b = ls;
                }
                if (b < 0) b = WordBreak(ed, min, q);
                if (b < 0) b = q;
                parts.Add(b - p);
                p = b;
            }
            return parts;
        }

        // Inizio di una riga in (min, q] che segue una riga vuota, cercando all'indietro; -1 se non c'è
        static long ParagraphBreak(SciEditor ed, long min, long q)
        {
            for (long l = ed.LineFromPosition(q); l > 0; l--)
            {
                long ls = ed.Send(Sci.SCI_POSITIONFROMLINE, l);
                if (ls <= min) break;
                long ps = ed.Send(Sci.SCI_POSITIONFROMLINE, l - 1), pe = ed.Send(Sci.SCI_GETLINEENDPOSITION, l - 1);
                if (pe - ps <= 100 && ed.GetTextRange(ps, pe).Trim().Length == 0) return ls;
            }
            return -1;
        }

        // Dopo l'ultima fine di frase in (min, q], o almeno dopo l'ultimo spazio; -1 se non c'è
        static long WordBreak(SciEditor ed, long min, long q)
        {
            string t = ed.GetTextRange(min, q);
            int i = Math.Max(Math.Max(t.LastIndexOf(". "), t.LastIndexOf("! ")), Math.Max(t.LastIndexOf("? "), t.LastIndexOf("; ")));
            if (i >= 0) i += 2;
            else { i = t.LastIndexOfAny(new[] { ' ', '\t' }); if (i >= 0) i++; }
            if (i <= 0) return -1;
            return min + Encoding.UTF8.GetByteCount(t.Substring(0, i));
        }

        // ---- Lavoro in background ----

        // Esegue f sul thread dell'interfaccia e aspetta il risultato; default se la finestra non c'è più
        T OnUi<T>(Func<T> f)
        {
            try { return (T)Invoke(f); }
            catch (ObjectDisposedException) { return default(T); }
            catch (InvalidOperationException) { return default(T); }
        }

        void AiStatusFromWorker(AiJob job, string text)
        {
            try { BeginInvoke(new Action(delegate { if (aiJob == job) ShowAiStatus(text); })); } catch { }
        }

        // Sostituzione: una parte alla volta, ognuna scritta al posto dell'originale mentre arriva
        AiResult RunReplaceParts(AiJob job, Action<string> onText)
        {
            AiResult last = new AiResult();
            for (int i = 0; i < job.Parts; i++)
            {
                int part = i;
                string text = OnUi(() => StartPart(job, part));
                if (text == null) return new AiResult { Cancelled = true };
                if (text.Length == 0) continue;         // solo spazi: resta com'è
                last = job.Provider.Run(job.Action.BuildPart(text, i + 1, job.Parts), onText, job.Cts.Token);
                if (!OnUi(() => EndPart(job, last))) return last.Ok ? new AiResult { Cancelled = true } : last;
            }
            return last;
        }

        // Riassunto o spiegazione: un testo che ci sta va in una sola richiesta; altrimenti appunti su ogni parte,
        // appunti condensati se sono ancora troppi, e il risultato dagli appunti
        AiResult RunReadParts(AiJob job, Action<string> onText)
        {
            var p = job.Provider;
            var cancel = job.Cts.Token;
            if (job.Parts == 1) return p.Run(job.Action.Build(job.PartText[0], null), onText, cancel);

            Action<string> ignore = x => { };
            var notes = new List<string>();
            for (int i = 0; i < job.Parts; i++)
            {
                AiStatusFromWorker(job, L.F("{0} legge il testo: parte {1} di {2}… Esc per fermare", "{0} is reading the text: part {1} of {2}… Press Esc to stop", p.Name, i + 1, job.Parts));
                var r = p.Run(AiAction.BuildNotes(job.PartText[i], i + 1, job.Parts), ignore, cancel);
                if (!r.Ok) return r;
                job.PartText[i] = null;
                notes.Add(r.Text.Trim());
            }
            int max = (p.SendsTextOutside ? CloudMaxChars : LocalMaxChars) - 2000;
            AiResult fail;
            string all = CondenseNotes(p, notes, max, s => AiStatusFromWorker(job, s), cancel, out fail);
            if (all == null) return fail;
            AiStatusFromWorker(job, L.F("{0} sta scrivendo… Esc per fermare", "{0} is writing… Press Esc to stop", p.Name));
            return p.Run(job.Action.BuildFromNotes(all), onText, cancel);
        }

        // Appunti su tutte le parti, condensati finché stanno in max caratteri (al massimo 4 giri, poi si tagliano).
        // null se una richiesta fallisce o viene fermata (fail dice perché).
        static string CondenseNotes(IAiProvider p, List<string> notes, int max, Action<string> status, CancellationToken cancel, out AiResult fail)
        {
            fail = null;
            Action<string> ignore = x => { };
            for (int round = 0; round < 4 && notes.Sum(n => n.Length + 1) > max; round++)
            {
                // Gruppi di appunti consecutivi che stanno in una richiesta, ognuno condensato
                var groups = new List<string>();
                var cur = new StringBuilder();
                foreach (var n in notes)
                {
                    string t = n.Length > max ? n.Substring(0, max) : n;
                    if (cur.Length > 0 && cur.Length + t.Length + 1 > max) { groups.Add(cur.ToString()); cur.Clear(); }
                    cur.Append(t).Append('\n');
                }
                if (cur.Length > 0) groups.Add(cur.ToString());
                var merged = new List<string>();
                for (int g = 0; g < groups.Count; g++)
                {
                    status(L.F("{0} riordina gli appunti: {1} di {2}… Esc per fermare", "{0} is condensing the notes: {1} of {2}… Press Esc to stop", p.Name, g + 1, groups.Count));
                    var r = p.Run(AiAction.BuildMerge(groups[g]), ignore, cancel);
                    if (!r.Ok) { fail = r; return null; }
                    merged.Add(r.Text.Trim());
                }
                notes = merged;
            }
            string all = string.Join("\n", notes);
            return all.Length > max ? all.Substring(0, max) : all;
        }

        // Comincia la parte i: il suo testo esce dal documento (restano gli spazi iniziali) e la risposta si scrive al suo
        // posto. Restituisce il testo da mandare, "" se la parte è fatta solo di spazi, null se il lavoro è stato fermato.
        string StartPart(AiJob job, int i)
        {
            if (job.Detached || job.Doc.Closed || job.Cts.IsCancellationRequested || aiJob != job) return null;
            var ed = job.Ed;
            long p = job.Pos, len = job.PartLen[i];
            string t = ed.GetTextRange(p, p + len);
            int a = 0;
            while (a < t.Length && char.IsWhiteSpace(t[a])) a++;
            if (a == t.Length)
            {
                job.Pos = job.DoneEnd = p + len;
                job.Done++;
                return "";
            }
            int z = t.Length;
            while (z > a && char.IsWhiteSpace(t[z - 1])) z--;
            string lead = t.Substring(0, a);
            job.PartStart = p;
            job.PartOriginal = t;
            job.Trail = t.Substring(z);
            ed.ReadOnly = false;
            ed.Send(Sci.SCI_DELETERANGE, p, len);
            if (lead.Length > 0) ed.SendStr(Sci.SCI_INSERTTEXT, p, lead);
            ed.ReadOnly = true;
            job.Changed = true;
            job.Pos = job.BodyStart = p + Encoding.UTF8.GetByteCount(lead);
            job.SeenText = false;
            job.Held = "";
            lock (job.Pending) job.Pending.Clear();
            ed.Send(Sci.SCI_GOTOPOS, job.Pos);
            ShowAiStatus(job.Parts > 1
                ? L.F("{0} sta scrivendo… parte {1} di {2} · Esc per fermare", "{0} is writing… part {1} of {2} · Press Esc to stop", job.Provider.Name, i + 1, job.Parts)
                : L.F("{0} sta scrivendo… Esc per fermare", "{0} is writing… Press Esc to stop", job.Provider.Name));
            return t.Substring(a, z - a);
        }

        // Fine della parte: se è andata bene si rimettono gli spazi finali originali; altrimenti torna il testo originale
        // della parte. false = fermarsi (r.Error spiega perché).
        bool EndPart(AiJob job, AiResult r)
        {
            if (job.Detached || job.Doc.Closed) return false;
            FlushAi(job);
            lock (job.Pending) job.Pending.Clear();
            job.Held = "";
            var ed = job.Ed;
            bool wrote = job.Pos > job.BodyStart;
            if (r.Ok && !r.Truncated && wrote)
            {
                if (job.Trail.Length > 0)
                {
                    ed.ReadOnly = false;
                    ed.SendStr(Sci.SCI_INSERTTEXT, job.Pos, job.Trail);
                    ed.ReadOnly = true;
                    job.Pos += Encoding.UTF8.GetByteCount(job.Trail);
                }
                job.DoneEnd = job.Pos;
                job.Done++;
                return true;
            }
            ed.ReadOnly = false;
            ed.Send(Sci.SCI_DELETERANGE, job.PartStart, job.Pos - job.PartStart);
            ed.SendStr(Sci.SCI_INSERTTEXT, job.PartStart, job.PartOriginal);
            ed.ReadOnly = true;
            job.Pos = job.PartStart + Encoding.UTF8.GetByteCount(job.PartOriginal);
            if (r.Ok && r.Truncated)
                r.Error = L.T("La risposta è stata interrotta perché troppo lunga. Prova con una parte più piccola del testo.",
                              "The answer was cut off because it was too long. Try with a smaller part of the text.");
            else if (r.Ok)
                r.Error = L.F("{0} non ha scritto nulla.", "{0} wrote nothing.", job.Provider.Name);
            return false;
        }

        // ---- Scrittura nel documento ----

        void AiTick()
        {
            if (aiJob != null) FlushAi(aiJob);
            else aiTimer.Stop();
        }

        void FlushAi(AiJob job)
        {
            if (job.Detached || job.Doc.Closed) return;
            string s;
            lock (job.Pending)
            {
                if (job.Pending.Length == 0) return;
                s = job.Pending.ToString();
                // Un "\r" finale può essere la prima metà di "\r\n": si aspetta il pezzo dopo
                if (s.EndsWith("\r")) { s = s.Substring(0, s.Length - 1); job.Pending.Clear().Append('\r'); }
                else job.Pending.Clear();
            }
            if (!job.SeenText)
            {
                s = s.TrimStart();          // i modelli a volte partono con righe vuote
                if (s.Length == 0) return;
                job.SeenText = true;
            }
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            if (job.Eol != "\n") s = s.Replace("\n", job.Eol);
            if (job.Output != AiOutput.NewTab)
            {
                // Gli spazi finali si tengono da parte: alla fine della parte valgono quelli del testo originale
                s = job.Held + s;
                int z = s.Length;
                while (z > 0 && char.IsWhiteSpace(s[z - 1])) z--;
                job.Held = s.Substring(z);
                s = s.Substring(0, z);
            }
            if (s.Length == 0) return;

            var ed = job.Ed;
            ed.ReadOnly = false;
            ed.SendStr(Sci.SCI_INSERTTEXT, job.Pos, s);
            ed.ReadOnly = true;
            job.Pos += Encoding.UTF8.GetByteCount(s);
            job.Changed = true;
            ed.Send(Sci.SCI_GOTOPOS, job.Pos);
        }

        void FinishAi(AiJob job, AiResult res)
        {
            if (aiJob == job) { aiJob = null; aiTimer.Stop(); }
            if (job.Detached || job.Doc.Closed) return;

            var ed = job.Ed;
            bool replace = job.Output != AiOutput.NewTab;
            if (!replace)
            {
                FlushAi(job);
                lock (job.Pending) job.Pending.Clear();
            }
            ed.ReadOnly = false;
            if (replace) ed.Send(Sci.SCI_ENDUNDOACTION);
            ed.ReadOnly = job.WasReadOnly;

            if (!replace && res.Ok && job.Pos == job.Start)
                res.Error = L.F("{0} non ha scritto nulla.", "{0} wrote nothing.", job.Provider.Name);

            if (res.Ok)
            {
                // Il risultato resta selezionato (Ctrl+Z lo annulla), con la vista sul suo inizio; in una scheda nuova
                // basta il cursore all'inizio
                if (!replace) ed.Send(Sci.SCI_GOTOPOS, 0);
                else { ed.Send(Sci.SCI_SETSEL, job.Pos, job.Start); ed.Send(Sci.SCI_SCROLLCARET); }
                ShowAiStatus(!replace && res.Truncated
                    ? L.T("Risposta interrotta perché troppo lunga.", "Answer cut off because it was too long.")
                    : null);
                if (!replace && res.Truncated) AiMessage(L.T("La risposta è stata interrotta perché troppo lunga. Prova con una parte più piccola del testo.",
                                                             "The answer was cut off because it was too long. Try with a smaller part of the text."), MessageBoxIcon.Warning);
            }
            else if (replace && job.Done > 0 && job.Done < job.Parts)
            {
                // Fermato a metà di un testo lungo: le parti finite restano, selezionate
                ed.Send(Sci.SCI_SETSEL, job.DoneEnd, job.Start);
                ed.Send(Sci.SCI_SCROLLCARET);
                ShowAiStatus(null);
                string done = L.F("Elaborate {0} parti su {1}: sono selezionate, il resto del testo è com'era. Ctrl+Z riporta tutto il testo originale.",
                                  "{0} of {1} parts processed: they are selected, the rest of the text is as it was. Ctrl+Z brings back the whole original text.",
                                  job.Done, job.Parts);
                AiMessage((res.Cancelled ? "" : res.Error + "\n\n") + done, res.Cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            else
            {
                // Fermato o errore: il testo torna com'era (in una scheda nuova resta quello già scritto)
                if (replace && job.Changed) ed.Send(Sci.SCI_UNDO);
                if (!replace && job.Pos == 0 && IsBlank(job.Doc)) CloseDoc(job.Doc);
                ShowAiStatus(null);
                if (!res.Cancelled) AiMessage(res.Error, MessageBoxIcon.Warning);
            }
            if (job.Doc == active && !job.Doc.Closed) ed.Focus();
            UpdateStatus();
        }

        void StopAi()
        {
            var job = aiJob;
            if (job == null) return;
            job.Cts.Cancel();
            ShowAiStatus(L.T("Arresto…", "Stopping…"));
        }

        // Scheda o finestra che si chiude durante la scrittura: si ferma tutto e il testo torna com'era subito,
        // prima che la sessione venga salvata
        void AbortAi(Doc onlyFor)
        {
            var job = aiJob;
            if (job == null || (onlyFor != null && job.Doc != onlyFor)) return;
            aiJob = null;
            aiTimer.Stop();
            job.Cts.Cancel();
            job.Detached = true;
            if (job.Doc.Closed || job.Ed == null) return;
            var ed = job.Ed;
            ed.ReadOnly = false;
            if (job.Output != AiOutput.NewTab)
            {
                ed.Send(Sci.SCI_ENDUNDOACTION);
                if (job.Changed) ed.Send(Sci.SCI_UNDO);
            }
            ed.ReadOnly = job.WasReadOnly;
            ShowAiStatus(null);
        }

        void ShowAiStatus(string text)
        {
            sAi.Text = text ?? "";
            sAi.Visible = !string.IsNullOrEmpty(text);
        }

        void InitAi()
        {
            sAi.Visible = false;
            sAi.IsLink = false;
            sAi.ToolTipText = L.T("Clic per fermare", "Click to stop");
            sAi.Click += delegate { StopAi(); };
            aiTimer.Tick += delegate { AiTick(); };
        }
    }
}
