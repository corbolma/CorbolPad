// Finestra principale: il pannello "Chiedi all'assistente", a lato o sotto l'editor. È una conversazione:
// si può allegare il testo selezionato (o tutto il documento) e la risposta, che non tocca il documento
// mentre arriva, si inserisce poi con un clic (al cursore, al posto della selezione, in una scheda nuova).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppPad
{
    partial class MainForm
    {
        const string ChatSystem =
            "You are a helpful assistant built into AppPad, a plain-text editor. Each message starts with the list of tabs open in the editor; " +
            "the content of the tabs you can read follows between <document name=\"...\"> and </document> (part=\"selection\" marks selected text, " +
            "truncated=\"yes\" a document that was cut). A tab too long to be read whole is given as several excerpts of the same document, " +
            "each with its line numbers: the passages that contain words of the question and the part around the cursor; or, for a general question, " +
            "as notes taken from all its parts (answer from the notes as if you had read the document). Answer from them; " +
            "if the answer may be in parts you were not given, say so and suggest asking with more specific words or selecting the part. Use those documents to answer and never ask the user to paste a text that is already there. " +
            "If the user mentions a tab whose content is not included, say so and suggest selecting \"all open tabs\" in the panel. Answer in the language of the user's message unless asked otherwise. Be concise. " +
            "Use plain text: no Markdown headings, bold or tables; simple lists with lines starting with \"- \" are fine. " +
            "When the user asks you to write, rewrite, translate or correct text, reply with the resulting text only, " +
            "without introductions or comments, because it may be inserted into the document as it is. " +
            "When you rewrite, correct or translate attached text, keep its line breaks, paragraphs and lists as they are.";

        readonly Panel aiPanel = new Panel();
        readonly Splitter aiSplit = new Splitter();
        readonly Label aiTitle = new Label();
        readonly RichTextBox aiLog = new RichTextBox();
        readonly TextBox aiInput = new TextBox();
        readonly ComboBox aiContext = new ComboBox();
        readonly Label aiContextLbl = new Label();
        readonly Button aiSend = new Button(), aiNew = new Button(), aiDockBtn = new Button(), aiClose = new Button();
        readonly Button aiInsert = new Button(), aiReplace = new Button(), aiCopy = new Button(), aiNewTab = new Button();
        readonly FlowLayoutPanel aiAnswerRow = new FlowLayoutPanel();
        readonly System.Windows.Forms.Timer aiChatTimer = new System.Windows.Forms.Timer { Interval = 80 };
        readonly List<AiMessage> aiHistory = new List<AiMessage>();
        string aiLastAnswer;
        bool aiPanelBottom;
        ChatJob aiChat;

        class ChatJob
        {
            public IAiProvider Provider;
            public string Question;
            public bool SeenText;
            public readonly StringBuilder Answer = new StringBuilder();
            public readonly StringBuilder Pending = new StringBuilder();
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        }

        // ---- Costruzione ----

        void BuildAiPanel()
        {
            aiPanelBottom = Settings.Get("ai.panel.dock", "right") == "bottom";
            aiPanel.Visible = aiSplit.Visible = false;
            aiPanel.Padding = new Padding(S(6), S(4), S(6), S(6));

            // Intestazione: titolo e pulsanti
            var head = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            aiTitle.AutoSize = true; aiTitle.Anchor = AnchorStyles.Left; aiTitle.AutoEllipsis = true;
            aiTitle.Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
            SetupFlat(aiNew, L.T("Nuova", "New"), L.T("Nuova conversazione", "New conversation"));
            SetupFlat(aiDockBtn, "", "");
            SetupFlat(aiClose, "×", L.T("Chiudi il pannello (Esc)", "Close the panel (Esc)"));
            aiClose.MinimumSize = new Size(S(28), S(26));
            head.Controls.Add(aiTitle, 0, 0);
            head.Controls.Add(aiNew, 1, 0);
            head.Controls.Add(aiDockBtn, 2, 0);
            head.Controls.Add(aiClose, 3, 0);

            // Conversazione
            aiLog.Dock = DockStyle.Fill;
            aiLog.ReadOnly = true;
            aiLog.BorderStyle = BorderStyle.None;
            aiLog.DetectUrls = false;
            aiLog.HideSelection = false;
            aiLog.Font = new Font("Segoe UI", 10f);
            aiLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            var logBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(6), S(4), S(2), S(4)), Margin = new Padding(0) };
            logBox.Controls.Add(aiLog);

            // Parte sotto: azioni sulla risposta, testo da allegare, domanda
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, S(4), 0, 0) };
            aiAnswerRow.AutoSize = true; aiAnswerRow.AutoSizeMode = AutoSizeMode.GrowAndShrink; aiAnswerRow.Dock = DockStyle.Fill; aiAnswerRow.Margin = new Padding(0);
            SetupFlat(aiInsert, L.T("Inserisci", "Insert"), L.T("Inserisce l'ultima risposta nel punto del cursore", "Inserts the last answer at the cursor"));
            SetupFlat(aiReplace, L.T("Sostituisci", "Replace"), L.T("Mette l'ultima risposta al posto del testo selezionato", "Puts the last answer in place of the selected text"));
            SetupFlat(aiNewTab, L.T("Nuova scheda", "New tab"), L.T("Apre l'ultima risposta in una scheda nuova", "Opens the last answer in a new tab"));
            SetupFlat(aiCopy, L.T("Copia", "Copy"), L.T("Copia l'ultima risposta negli appunti", "Copies the last answer to the clipboard"));
            aiAnswerRow.Controls.AddRange(new Control[] { aiInsert, aiReplace, aiNewTab, aiCopy });

            var ctxRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) };
            ctxRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ctxRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            aiContextLbl.Text = L.T("Legge:", "Reads:");
            aiContextLbl.AutoSize = true; aiContextLbl.Anchor = AnchorStyles.Left;
            aiContext.DropDownStyle = ComboBoxStyle.DropDownList;
            aiContext.FlatStyle = FlatStyle.Flat;
            aiContext.Dock = DockStyle.Fill;
            aiContext.Items.AddRange(new object[]
            {
                L.T("la scheda attiva (o il testo selezionato)", "the active tab (or the selected text)"),
                L.T("tutte le schede aperte", "all open tabs"),
                L.T("niente, solo i nomi delle schede", "nothing, only the tab names")
            });
            aiContext.SelectedIndex = Math.Max(0, Math.Min(2, Settings.Get("ai.panel.read", 0)));
            aiContext.SelectedIndexChanged += delegate { Settings.Set("ai.panel.read", aiContext.SelectedIndex); };
            ctxRow.Controls.Add(aiContextLbl, 0, 0);
            ctxRow.Controls.Add(aiContext, 1, 0);

            var askRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) };
            askRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            askRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            aiInput.Multiline = true;
            aiInput.AcceptsReturn = false;
            aiInput.ScrollBars = ScrollBars.None;
            aiInput.Height = S(64);
            aiInput.Dock = DockStyle.Fill;
            aiInput.Font = new Font("Segoe UI", 10f);
            SetupFlat(aiSend, L.T("Invia", "Send"), L.T("Invio per inviare, Maiusc+Invio per andare a capo", "Enter to send, Shift+Enter for a new line"));
            aiSend.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            aiSend.MinimumSize = new Size(S(70), S(30));
            askRow.Controls.Add(aiInput, 0, 0);
            askRow.Controls.Add(aiSend, 1, 0);

            bottom.Controls.Add(aiAnswerRow, 0, 0);
            bottom.Controls.Add(ctxRow, 0, 1);
            bottom.Controls.Add(askRow, 0, 2);

            aiPanel.Controls.Add(logBox);
            aiPanel.Controls.Add(bottom);
            aiPanel.Controls.Add(head);

            aiSplit.MinSize = S(120);
            aiSplit.SplitterMoved += delegate
            {
                if (aiPanelBottom) Settings.Set("ai.panel.height", (int)(aiPanel.Height / scale));
                else Settings.Set("ai.panel.width", (int)(aiPanel.Width / scale));
            };

            aiInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt) { e.SuppressKeyPress = true; SendChat(); }
                else if (e.KeyCode == Keys.Enter && e.Shift) { e.SuppressKeyPress = true; aiInput.SelectedText = "\r\n"; }
                else if (e.KeyCode == Keys.A && e.Control) { e.SuppressKeyPress = true; aiInput.SelectAll(); }
            };
            aiSend.Click += delegate { SendChat(); };
            aiNew.Click += delegate { NewChat(); };
            aiDockBtn.Click += delegate { aiPanelBottom = !aiPanelBottom; Settings.Set("ai.panel.dock", aiPanelBottom ? "bottom" : "right"); LayoutAiPanel(); aiInput.Focus(); };
            aiClose.Click += delegate { HideAiPanel(); };
            aiInsert.Click += delegate { UseAnswer(AiOutput.Insert); };
            aiReplace.Click += delegate { UseAnswer(AiOutput.Replace); };
            aiNewTab.Click += delegate { UseAnswer(AiOutput.NewTab); };
            aiCopy.Click += delegate { if (aiLastAnswer != null) try { Clipboard.SetText(aiLastAnswer); } catch { } };
            aiChatTimer.Tick += delegate { FlushChat(); };

            LayoutAiPanel();
            NewChat();
        }

        void SetupFlat(Button b, string text, string tip)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, S(26));
            b.Margin = new Padding(0, S(2), S(4), S(2));
            b.UseVisualStyleBackColor = false;
            if (!string.IsNullOrEmpty(tip)) tips.SetToolTip(b, tip);
        }

        readonly ToolTip tips = new ToolTip();

        void LayoutAiPanel()
        {
            SuspendLayout();
            aiPanel.Dock = aiSplit.Dock = aiPanelBottom ? DockStyle.Bottom : DockStyle.Right;
            if (aiPanelBottom) aiPanel.Height = S(Math.Max(220, Settings.Get("ai.panel.height", 340)));
            else aiPanel.Width = S(Math.Max(220, Settings.Get("ai.panel.width", 400)));
            aiDockBtn.Text = aiPanelBottom ? L.T("A lato", "Side") : L.T("Sotto", "Bottom");
            tips.SetToolTip(aiDockBtn, aiPanelBottom ? L.T("Sposta il pannello a destra dell'editor", "Move the panel to the right of the editor")
                                                     : L.T("Sposta il pannello sotto l'editor", "Move the panel below the editor"));
            ResumeLayout();
        }

        void ApplyAiPanelTheme(Theme t)
        {
            aiPanel.BackColor = t.Chrome;
            aiTitle.ForeColor = aiContextLbl.ForeColor = t.ChromeText;
            aiLog.BackColor = t.EditorBack;
            aiLog.ForeColor = t.EditorFore;
            aiLog.Parent.BackColor = t.EditorBack;
            aiInput.BackColor = aiContext.BackColor = t.InputBack;
            aiInput.ForeColor = aiContext.ForeColor = t.InputFore;
            aiSplit.BackColor = t.Border;
            foreach (var b in new[] { aiSend, aiNew, aiDockBtn, aiClose, aiInsert, aiReplace, aiCopy, aiNewTab }) StyleFlat(b, t);
            RecolorChat();
        }

        // ---- Mostra e nascondi ----

        void ShowAiPanel()
        {
            string why;
            aiTitle.Text = AiProvider(out why) != null ? L.T("Assistente", "Assistant") + " · " + AiProviderName() : L.T("Assistente", "Assistant");
            aiPanel.Visible = aiSplit.Visible = true;
            aiInput.Focus();
            aiInput.SelectAll();
        }

        // F1 e il pulsante sulla barra dei menu: apre il pannello, ci porta il cursore, oppure lo chiude
        void ToggleAiPanel()
        {
            if (!aiPanel.Visible || !aiInput.Focused) ShowAiPanel();
            else HideAiPanel();
        }

        void HideAiPanel()
        {
            aiPanel.Visible = aiSplit.Visible = false;
            if (Ed != null) Ed.Focus();
        }

        string AiProviderName()
        {
            string why;
            var p = AiProvider(out why);
            return p != null ? p.Name : "";
        }

        // ---- Conversazione ----

        void NewChat()
        {
            if (aiChat != null) aiChat.Cts.Cancel();
            aiHistory.Clear();
            aiLastAnswer = null;
            ClearChat();
            AppendChat(L.T("Scrivi qui sotto una domanda o una richiesta e premi Invio (Maiusc+Invio per andare a capo). " +
                           "L'assistente legge la scheda attiva (o il testo selezionato); con \"Legge\" puoi fargli leggere tutte le schede aperte. " +
                           "La risposta si può poi inserire nel documento con i pulsanti. F1 apre e chiude il pannello.",
                           "Type a question or request below and press Enter (Shift+Enter for a new line). " +
                           "The assistant reads the active tab (or the selected text); with \"Reads\" it can read all open tabs. " +
                           "The answer can then be put into the document with the buttons. F1 opens and closes the panel."), ChatColor.Dim, false);
            UpdateChatButtons();
        }

        enum ChatColor { Normal, Dim, Accent, Error }

        Color ChatColorOf(ChatColor c)
        {
            var t = theme ?? Theme.Light;
            switch (c)
            {
                case ChatColor.Dim: return t.ChromeTextDim;
                case ChatColor.Accent: return t.Accent;
                case ChatColor.Error: return t.Error;
                default: return t.EditorFore;
            }
        }

        // Il contenuto della conversazione a pezzi con il loro stile: al cambio di tema si ridisegna
        class ChatPiece { public StringBuilder Text = new StringBuilder(); public ChatColor Color; public bool Bold; }
        readonly List<ChatPiece> chatPieces = new List<ChatPiece>();

        void AppendChat(string text, ChatColor c, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return;
            var last = chatPieces.Count > 0 ? chatPieces[chatPieces.Count - 1] : null;
            if (last != null && last.Color == c && last.Bold == bold) last.Text.Append(text);
            else { last = new ChatPiece { Color = c, Bold = bold }; last.Text.Append(text); chatPieces.Add(last); }
            Render(text, c, bold);
            aiLog.SelectionStart = aiLog.TextLength;
            aiLog.ScrollToCaret();
        }

        void Render(string text, ChatColor c, bool bold)
        {
            aiLog.SelectionStart = aiLog.TextLength;
            aiLog.SelectionLength = 0;
            aiLog.SelectionColor = ChatColorOf(c);
            aiLog.SelectionFont = bold ? chatBold : aiLog.Font;
            aiLog.SelectedText = text;
        }

        Font chatBoldFont;
        Font chatBold { get { return chatBoldFont ?? (chatBoldFont = new Font(aiLog.Font, FontStyle.Bold)); } }

        void ClearChat()
        {
            chatPieces.Clear();
            aiLog.Clear();
        }

        void RecolorChat()
        {
            if (chatPieces.Count == 0) return;
            aiLog.Clear();
            foreach (var p in chatPieces) Render(p.Text.ToString(), p.Color, p.Bold);
            aiLog.SelectionStart = aiLog.TextLength;
            aiLog.ScrollToCaret();
        }

        void UpdateChatButtons()
        {
            bool busy = aiChat != null;
            aiSend.Text = busy ? L.T("Ferma", "Stop") : L.T("Invia", "Send");
            bool has = !busy && !string.IsNullOrEmpty(aiLastAnswer);
            aiInsert.Enabled = aiReplace.Enabled = aiNewTab.Enabled = aiCopy.Enabled = has;
        }

        void SendChat()
        {
            if (aiChat != null) { aiChat.Cts.Cancel(); return; }
            string q = aiInput.Text.Trim();
            if (q.Length == 0) return;
            if (aiJob != null) { AppendChat("\n" + L.T("Attendi che l'assistente finisca di scrivere nel documento.", "Wait for the assistant to finish writing in the document.") + "\n", ChatColor.Error, false); return; }

            string why;
            var provider = AiProvider(out why);
            if (provider == null)
            {
                if (MessageBox.Show(this, why + "\n\n" + L.T("Aprire le impostazioni dell'assistente?", "Open the assistant settings?"),
                                    L.T("Assistente", "Assistant"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    ShowAiSettings();
                return;
            }

            // Le schede che l'assistente legge (sempre almeno i loro nomi)
            string note, error;
            string context = ReadTabs(q, (provider.SendsTextOutside ? CloudMaxChars : LocalMaxChars) - q.Length, provider.Name, out note, out error);
            var parts = chatParts;                  // scheda troppo lunga da leggere tutta, a parti
            int partsRoom = chatPartsRoom;
            chatParts = null;
            if (error != null) { AppendChat("\n" + error + "\n", ChatColor.Error, false); return; }

            if (provider.SendsTextOutside && !ConfirmCloud(provider)) return;

            // Domanda di adesso con le schede; prima, le domande e risposte precedenti finché ci stanno
            string user = context + "\n" + q;
            int budget = (provider.SendsTextOutside ? CloudMaxChars : LocalMaxChars) - user.Length - (parts != null ? partsRoom : 0);
            var history = new List<AiMessage>();
            for (int i = aiHistory.Count - 2; i >= 0; i -= 2)
            {
                int len = aiHistory[i].Text.Length + aiHistory[i + 1].Text.Length;
                if (len > budget) break;
                budget -= len;
                history.InsertRange(0, new[] { aiHistory[i], aiHistory[i + 1] });
            }
            var req = new AiRequest { System = ChatSystem };
            req.Messages.AddRange(history);
            req.Messages.Add(new AiMessage("user", user));

            var job = new ChatJob { Provider = provider, Question = q };
            aiChat = job;
            aiInput.Clear();
            aiTitle.Text = L.T("Assistente", "Assistant") + " · " + provider.Name;
            AppendChat((aiLog.TextLength > 0 ? "\n\n" : "") + L.T("Tu", "You") + "\n", ChatColor.Accent, true);
            AppendChat(q.Replace("\r\n", "\n") + "\n", ChatColor.Normal, false);
            if (note != null) AppendChat(note + "\n", ChatColor.Dim, false);
            AppendChat("\n" + provider.Name + "\n", ChatColor.Accent, true);
            UpdateChatButtons();

            var local = provider as LocalAi;
            Action<string> onStatus = st => { if (st != null) try { BeginInvoke(new Action(delegate { if (aiChat == job && !job.SeenText) ShowAiStatus(st); })); } catch { } };
            if (local != null) local.StatusChanged += onStatus;
            ShowAiStatus(L.F("{0} sta rispondendo… Esc per fermare", "{0} is answering… Press Esc to stop", provider.Name));

            var th = new Thread(delegate ()
            {
                AiResult res;
                try
                {
                    res = null;
                    if (parts != null)
                    {
                        // Prima gli appunti su ogni parte della scheda, poi la risposta dagli appunti
                        Action<string> status = st => { try { BeginInvoke(new Action(delegate { if (aiChat == job) ShowAiStatus(st); })); } catch { } };
                        var notes = new List<string>();
                        for (int i = 0; i < parts.Count && res == null; i++)
                        {
                            status(L.F("{0} legge la scheda: parte {1} di {2}… Esc per fermare", "{0} is reading the tab: part {1} of {2}… Press Esc to stop", provider.Name, i + 1, parts.Count));
                            var r = provider.Run(AiAction.BuildQuestionNotes(parts[i], q, i + 1, parts.Count), x => { }, job.Cts.Token);
                            if (!r.Ok) res = r;
                            else notes.Add(r.Text.Trim());
                            parts[i] = null;
                        }
                        if (res == null)
                        {
                            AiResult fail;
                            string all = CondenseNotes(provider, notes, partsRoom, status, job.Cts.Token, out fail);
                            if (all == null) res = fail;
                            else
                            {
                                var last = req.Messages[req.Messages.Count - 1];
                                req.Messages[req.Messages.Count - 1] = new AiMessage("user", last.Text.Replace(NotesMarker, all));
                                status(L.F("{0} sta rispondendo… Esc per fermare", "{0} is answering… Press Esc to stop", provider.Name));
                            }
                        }
                    }
                    if (res == null) res = provider.Run(req, chunk => { if (chunk != null) lock (job.Pending) job.Pending.Append(chunk); }, job.Cts.Token);
                }
                catch (Exception ex) { res = new AiResult { Error = ex.Message }; }
                if (local != null) local.StatusChanged -= onStatus;
                try { BeginInvoke(new Action(delegate { FinishChat(job, res); })); } catch { }
            });
            th.IsBackground = true;
            th.Name = "AppPad AI chat";
            th.Start();
            aiChatTimer.Start();
        }

        // Testo per l'assistente: l'elenco delle schede aperte e il contenuto di quelle da leggere (la selezione o la
        // scheda attiva, oppure tutte, anche se non ancora caricate). note dice cosa è stato letto; error se non ci sta.
        string ReadTabs(string question, int max, string providerName, out string note, out string error)
        {
            note = null; error = null;
            chatParts = null;
            var docs = Docs.Where(d => !d.Closed).ToList();
            var sb = new StringBuilder("Open tabs in the editor: ");
            sb.Append(string.Join(", ", docs.Select(d => "\"" + d.Title + "\"" + (d == active ? " (active)" : ""))));
            sb.Append(".\n");
            int mode = aiContext.SelectedIndex;
            if (mode == 2 || active == null) return sb.ToString();

            if (mode == 0)
            {
                var ed = Ed;
                if (ed == null) { note = L.F("(la scheda «{0}» è ancora in caricamento: non letta)", "(the tab “{0}” is still loading: not read)", active.Title); return sb.ToString(); }
                long s = ed.SelectionStart, e = ed.SelectionEnd;
                bool sel = e > s;
                if (!sel) { s = 0; e = ed.Length; }
                if (e == s) { note = L.F("(la scheda «{0}» è vuota)", "(the tab “{0}” is empty)", active.Title); return sb.ToString(); }
                bool partial = false;
                if (e - s > (long)max * 4 || ed.Send(Sci.SCI_COUNTCHARACTERS, s, e) > max)
                {
                    if (sel)
                    {
                        error = L.F("Il testo selezionato è troppo lungo per {0}: al massimo {1} caratteri. Selezionane una parte più piccola.",
                                    "The selected text is too long for {0}: {1} characters at most. Select a smaller part.", providerName, Util.N(max));
                        return null;
                    }
                    // Scheda intera troppo lunga: si leggono i passaggi con le parole della domanda e la parte intorno al
                    // cursore, lasciando spazio alle altre schede nominate nella domanda (fino a metà del totale)
                    long others = 0;
                    foreach (var d in docs)
                        if (d != active && Mentions(question, d.Title)) others += DocSize(d) + d.Title.Length + 160;
                    int reserve = (int)Math.Min(others, max / 2);
                    note = ReadLongTab(sb, ed, question, docs, Math.Max(1000, max - sb.Length - 200 - reserve), providerName);
                    partial = true;
                }
                if (!partial)
                {
                    string t = ed.GetTextRange(s, e);
                    AppendDoc(sb, active, t, sel ? "selection" : null, false);
                    note = sel ? L.F("(letto il testo selezionato in «{0}»: {1} caratteri)", "(read the selected text in “{0}”: {1} characters)", active.Title, Util.N(t.Length))
                               : L.F("(letta la scheda «{0}»: {1} caratteri)", "(read the tab “{0}”: {1} characters)", active.Title, Util.N(t.Length));
                }
                // Le altre schede nominate nella domanda ("confronta con note.txt") si leggono anche loro, se c'è spazio
                int room = max - sb.Length - (chatParts != null ? chatPartsRoom : 0);
                foreach (var d in docs)
                {
                    if (d == active || !Mentions(question, d.Title)) continue;
                    bool truncated = false;
                    string o = room > 200 ? DocText(d, room - 100, out truncated) : null;
                    if (o == null) { note += "\n" + L.F("(la scheda «{0}» non è stata letta: non c'è spazio o è in caricamento)", "(the tab “{0}” was not read: no room or still loading)", d.Title); continue; }
                    AppendDoc(sb, d, o, null, truncated);
                    room -= o.Length + d.Title.Length + 60;
                    note += "\n" + (truncated ? L.F("(letta in parte la scheda «{0}», nominata nella domanda)", "(partly read the tab “{0}”, mentioned in the question)", d.Title)
                                              : L.F("(letta anche la scheda «{0}», nominata nella domanda: {1} caratteri)", "(also read the tab “{0}”, mentioned in the question: {1} characters)", d.Title, Util.N(o.Length)));
                }
                return sb.ToString();
            }

            // Tutte le schede: prima quella attiva, poi le altre nell'ordine, finché c'è spazio
            int left = max - sb.Length;
            var read = new List<string>();
            var skipped = new List<string>();
            int total = 0;
            foreach (var d in new[] { active }.Concat(docs.Where(x => x != active)))
            {
                if (left < 200) { skipped.Add(d.Title); continue; }
                bool truncated;
                string t = DocText(d, left - 100, out truncated);
                if (t == null) { skipped.Add(d.Title); continue; }
                AppendDoc(sb, d, t, null, truncated);
                left -= t.Length + d.Title.Length + 60;
                total += t.Length;
                read.Add(truncated ? L.F("{0} (in parte)", "{0} (partly)", d.Title) : d.Title);
            }
            note = L.F("(lette {0} schede, {1} caratteri: {2})", "(read {0} tabs, {1} characters: {2})", read.Count, Util.N(total), string.Join(", ", read));
            if (skipped.Count > 0)
                note += "\n" + L.F("(non lette, per mancanza di spazio o perché in caricamento: {0})", "(not read, for lack of space or still loading: {0})", string.Join(", ", skipped));
            return sb.ToString();
        }

        // ---- Scheda troppo lunga: passaggi che contengono le parole della domanda ----

        const int SCI_GETCHARAT = 2007;
        const int MaxChatParts = 12;                    // oltre, una scheda troppo lunga non si legge tutta dal pannello
        const string NotesMarker = "\u0001NOTES\u0001"; // al suo posto vanno gli appunti sulla scheda letta a parti
        List<string> chatParts;                         // impostati da ReadTabs per SendChat
        int chatPartsRoom;
        const int SCFIND_WORDSTART = 0x00100000;

        static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // italiano
            "come", "cosa", "dove", "quando", "quale", "quali", "quanto", "quanti", "quante", "perché", "perche", "questo", "questa",
            "questi", "queste", "quello", "quella", "quelli", "quelle", "sono", "essere", "stato", "stata", "hanno", "fare", "fammi",
            "dimmi", "dammi", "puoi", "potresti", "vorrei", "voglio", "della", "delle", "dello", "degli", "nella", "nelle", "nello",
            "negli", "alla", "alle", "allo", "agli", "dalla", "dalle", "dagli", "sulla", "sulle", "sugli", "anche", "ancora", "molto",
            "tutto", "tutti", "tutte", "ogni", "altro", "altri", "altre", "solo", "però", "dopo", "prima", "sempre", "testo", "file",
            "documento", "scheda", "schede", "parte", "parti", "riga", "righe", "trova", "trovami", "cerca", "spiega", "spiegami",
            "riassumi", "elenca", "mostra", "scrivi", "esiste", "esistono", "dentro", "contiene", "parla", "parlano", "dice", "dicono",
            "detto", "qualche", "nessun", "nessuno", "volte", "c'è", "cos'è", "dov'è", "presente", "presenti", "riguardo", "secondo",
            // inglese
            "what", "where", "when", "which", "whom", "whose", "does", "this", "that", "these", "those", "there", "here", "with",
            "from", "about", "into", "have", "been", "were", "will", "would", "could", "should", "please", "tell", "show", "find",
            "explain", "summarize", "summarise", "list", "text", "document", "line", "lines", "part", "some", "many", "much", "more",
            "most", "also", "only", "just", "than", "then", "them", "they", "their", "your", "make", "give", "contain", "contains",
            "mention", "mentions", "says", "said", "times", "anything", "something", "every",
            "short", "brief", "briefly", "summary", "detail", "details", "detailed", "simple", "simply", "words", "overview", "general",
            // italiano: come rispondere, non cosa cercare
            "breve", "brevemente", "sintesi", "sintetico", "riassunto", "dettaglio", "dettagli", "dettagliato", "dettagliata",
            "semplice", "semplici", "semplicemente", "parole", "spiegazione", "spiegare", "spieghi", "generale", "insomma", "veloce",
            "velocemente", "punti", "elenco", "italiano", "inglese", "english", "italian"
        };

        // Le parole da cercare: le frasi tra virgolette così come sono, poi le parole di almeno 4 lettere (o con cifre,
        // o sigle in maiuscolo) che non siano parole comuni o nomi di schede. Le parole lunghe perdono la vocale finale
        // ("fatture" trova anche "fattura").
        // shown riceve le parole come scritte nella domanda, per la nota nel pannello.
        static List<string> Keywords(string question, IEnumerable<Doc> docs, out List<string> shown)
        {
            var res = new List<string>();
            shown = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in docs)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(d.Title, @"[\p{L}\p{N}_]+"))
                    names.Add(m.Value);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(question, "\"([^\"]{3,80})\"|«([^»]{3,80})»|“([^”]{3,80})”"))
            {
                string p = (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).Trim();
                if (p.Length >= 3) { res.Add(p); shown.Add("\"" + p + "\""); }
            }
            var words = new List<string[]>();
            // Parole senza apostrofo: "l'IBAN" dà "l" e "IBAN"
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(question, @"[\p{L}\p{N}_]+"))
            {
                string w = m.Value, orig = w;
                bool digit = w.Any(char.IsDigit), caps = w.Length >= 2 && w.All(c => char.IsUpper(c) || char.IsDigit(c));
                if (w.Length < 4 && !(digit && w.Length >= 2) && !caps) continue;
                if (StopWords.Contains(w) || names.Contains(w)) continue;
                if (w.Length >= 6 && !digit && "aeiouàèéìòù".IndexOf(char.ToLowerInvariant(w[w.Length - 1])) >= 0) w = w.Substring(0, w.Length - 1);
                if (!words.Any(x => string.Equals(x[0], w, StringComparison.OrdinalIgnoreCase))) words.Add(new[] { w, orig });
            }
            // Al massimo 10 parole, le più lunghe (di solito le più specifiche)
            foreach (var w in words.OrderByDescending(x => x[0].Length).Take(10)) { res.Add(w[0]); shown.Add(w[1]); }
            return res;
        }

        // Legge una scheda troppo lunga: i blocchi con più parole della domanda (le parole rare contano di più) fino a
        // tre quarti dello spazio, e la parte intorno al cursore nel resto. Restituisce la nota per il pannello.
        string ReadLongTab(StringBuilder sb, SciEditor ed, string question, List<Doc> docs, int budget, string providerName)
        {
            long length = ed.Length, lines = ed.LineCount;
            var ranges = new List<long[]>();         // [inizio, fine, 1 = passaggio / 0 = cursore]
            List<string> shown;
            var words = Keywords(question, docs, out shown);
            var found = new List<string>();
            int used = 0;
            if (words.Count > 0)
            {
                long block = Math.Max(1000, Math.Min(6000, budget / 8));
                var blocks = new Dictionary<long, Dictionary<int, int>>();
                var hits = new int[words.Count];
                Cursor = Cursors.WaitCursor;
                try
                {
                    for (int k = 0; k < words.Count; k++)
                    {
                        // Le frasi tra virgolette si cercano ovunque, le parole solo a inizio parola
                        ed.Send(Sci.SCI_SETSEARCHFLAGS, k < words.Count && words[k].Contains(" ") ? 0 : SCFIND_WORDSTART);
                        long p = 0;
                        while (hits[k] < 20000)
                        {
                            ed.Send(Sci.SCI_SETTARGETRANGE, p, length);
                            long pos = ed.SendText(Sci.SCI_SEARCHINTARGET, words[k]);
                            if (pos < 0) break;
                            hits[k]++;
                            Dictionary<int, int> b;
                            if (!blocks.TryGetValue(pos / block, out b)) blocks[pos / block] = b = new Dictionary<int, int>();
                            int c; b.TryGetValue(k, out c); b[k] = c + 1;
                            long te = ed.Send(Sci.SCI_GETTARGETEND);
                            p = te > pos ? te : pos + 1;
                        }
                    }
                }
                finally { Cursor = Cursors.Default; }
                for (int k = 0; k < words.Count; k++) if (hits[k] > 0) found.Add(shown[k]);

                // Peso di una parola: alto se compare in pochi blocchi
                double total = Math.Max(1, length / block);
                var weight = new double[words.Count];
                for (int k = 0; k < words.Count; k++)
                {
                    int inBlocks = blocks.Values.Count(b => b.ContainsKey(k));
                    // Una parola presente quasi ovunque non aiuta a scegliere ("codice" in un file di codice)
                    weight[k] = inBlocks == 0 || (total >= 20 && inBlocks >= total * 0.3) ? 0 : Math.Log(1 + total / inBlocks);
                }
                blocks = blocks.Where(kv => kv.Value.Keys.Any(k => weight[k] > 0)).ToDictionary(kv => kv.Key, kv => kv.Value);
                long caretBlock = ed.CurrentPos / block;
                var best = blocks.Select(kv => new { Block = kv.Key, Score = kv.Value.Sum(h => weight[h.Key] * (1 + 0.1 * Math.Min(h.Value, 5))) })
                                 .OrderByDescending(x => x.Score).ThenBy(x => Math.Abs(x.Block - caretBlock));
                int passBudget = budget * 3 / 4;
                foreach (var x in best)
                {
                    if (used > passBudget - 300) break;
                    long s, e;
                    BlockRange(ed, x.Block * block, Math.Min(length, (x.Block + 1) * block), block, out s, out e);
                    if (ranges.Any(r => s < r[1] && e > r[0])) continue;   // già preso con un blocco vicino
                    int n = (int)ed.Send(Sci.SCI_COUNTCHARACTERS, s, e);
                    if (used + n > passBudget) continue;
                    used += n;
                    ranges.Add(new[] { s, e, 1L });
                }
            }

            // Nessun passaggio utile (domanda generica come "spiegalo"): se non è enorme, la scheda si legge tutta a parti,
            // con appunti utili alla domanda, e si risponde dagli appunti
            if (ranges.Count == 0)
            {
                var lens = SplitParts(ed, 0, length, budget);
                if (lens.Count <= MaxChatParts)
                {
                    chatParts = new List<string>();
                    long p = 0;
                    foreach (long len in lens) { chatParts.Add(ed.GetTextRange(p, p + len)); p += len; }
                    chatPartsRoom = budget;
                    sb.Append("\n<document name=\"").Append(active.Title.Replace("\"", "'"))
                      .Append("\" part=\"notes on the whole tab, which was read in ").Append(lens.Count).Append(" parts because it is too long\">\n")
                      .Append(NotesMarker).Append("\n</document>\n");
                    string n = L.F("(la scheda «{0}» è troppo lunga per {1} da leggere in una volta: la legge tutta in {2} parti prendendo appunti, poi risponde dagli appunti)",
                                   "(the tab “{0}” is too long for {1} to read at once: it reads all of it in {2} parts taking notes, then answers from the notes)",
                                   active.Title, providerName, lens.Count);
                    if (providerName == LocalAi.Instance.Name)
                        n += "\n" + L.T("(con l'assistente locale può volerci qualche minuto)", "(with the local assistant this may take a few minutes)");
                    return n;
                }
            }

            // La parte intorno al cursore con lo spazio rimasto, senza ripetere i passaggi già presi
            long cs, ce;
            AroundCaret(ed, Math.Max(500, budget - used - 200 * (ranges.Count + 1)), out cs, out ce);
            int passages = ranges.Count;             // anche quelli che finiscono dentro la parte intorno al cursore
            ranges.RemoveAll(r => r[0] < ce && r[1] > cs && r[0] >= cs && r[1] <= ce);
            foreach (var r in ranges) { if (r[0] < cs && r[1] > cs) cs = r[1]; if (r[0] < ce && r[1] > ce) ce = r[0]; }
            if (ce > cs) ranges.Add(new[] { cs, ce, 0L });
            ranges.Sort((a, b) => a[0].CompareTo(b[0]));

            foreach (var r in ranges)
            {
                long first = ed.LineFromPosition(r[0]) + 1, last = ed.LineFromPosition(Math.Max(r[0], r[1] - 1)) + 1;
                string what = r[2] == 1 ? "passage containing words of the question" : "around the cursor";
                AppendDoc(sb, active, ed.GetTextRange(r[0], r[1]), "lines " + first + "-" + last + " of " + lines + ", " + what, true);

            }
            string note;
            if (passages == 1)
                note = L.F("(la scheda «{0}» è troppo lunga per {1}: letto il passaggio che contiene le parole della domanda ({2}) e la parte intorno al cursore)",
                           "(the tab “{0}” is too long for {1}: read the passage containing words of the question ({2}) and the part around the cursor)",
                           active.Title, providerName, string.Join(", ", found));
            else if (passages > 1)
                note = L.F("(la scheda «{0}» è troppo lunga per {1}: letti {2} passaggi che contengono le parole della domanda ({3}) e la parte intorno al cursore)",
                           "(the tab “{0}” is too long for {1}: read {2} passages containing words of the question ({3}) and the part around the cursor)",
                           active.Title, providerName, passages, string.Join(", ", found));
            else
            {
                long first = ed.LineFromPosition(cs) + 1, last = ed.LineFromPosition(Math.Max(cs, ce - 1)) + 1;
                note = L.F("(la scheda «{0}» è troppo lunga per {1}: letta solo la parte intorno al cursore, righe {2}–{3} di {4}. Per un'altra parte, selezionala o spostaci il cursore)",
                           "(the tab “{0}” is too long for {1}: read only the part around the cursor, lines {2}–{3} of {4}. For another part, select it or move the cursor there)",
                           active.Title, providerName, Util.N(first), Util.N(last), Util.N(lines));
                if (found.Count < words.Count)
                    note += "\n" + L.F("(nessuna di queste parole è nel testo: {0})", "(none of these words is in the text: {0})",
                                       string.Join(", ", shown.Where((w, k) => !found.Contains(w))));
                note += "\n" + L.T("(per una domanda su tutto il testo usa Assistente > Riassumi o Spiega, che leggono anche i testi lunghi)",
                                   "(for a question about the whole text use Assistant > Summarize or Explain, which also read long texts)");
            }
            return note;
        }

        // Un blocco [s, e) allargato a righe intere; se le righe sono troppo lunghe (file su una riga sola) si resta
        // sul blocco, solo allineato all'inizio dei caratteri
        static void BlockRange(SciEditor ed, long s, long e, long block, out long rs, out long re)
        {
            long lines = ed.LineCount, length = ed.Length;
            long ls = ed.Send(Sci.SCI_POSITIONFROMLINE, ed.LineFromPosition(s));
            long le1 = ed.LineFromPosition(Math.Max(s, e - 1)) + 1;
            long le = le1 >= lines ? length : ed.Send(Sci.SCI_POSITIONFROMLINE, le1);
            s = s - ls <= block / 2 ? ls : CharStart(ed, s);
            e = le - e <= block / 2 ? le : CharStart(ed, e);
            rs = s; re = Math.Max(s, e);
        }

        // Primo inizio di carattere UTF-8 da p in avanti (salta i byte di continuazione 10xxxxxx)
        static long CharStart(SciEditor ed, long p)
        {
            long length = ed.Length;
            while (p < length && (ed.Send(SCI_GETCHARAT, p) & 0xC0) == 0x80) p++;
            return p;
        }

        // La domanda nomina la scheda: il nome intero ("note.txt") o senza estensione ("note", almeno 3 lettere, parola intera)
        static bool Mentions(string question, string title)
        {
            if (question.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string bare = Path.GetFileNameWithoutExtension(title);
            if (bare.Length < 3 || bare == title) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(question, @"(?<![\w])" + System.Text.RegularExpressions.Regex.Escape(bare) + @"(?![\w])",
                                                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Parte del documento intorno al cursore, a righe intere, di al massimo max caratteri: si allarga una riga
        // alla volta, alternando sopra e sotto. Una sola riga più lunga di max si taglia intorno al cursore.
        static void AroundCaret(SciEditor ed, int max, out long s, out long e)
        {
            long lines = ed.LineCount, length = ed.Length, caret = ed.CurrentPos;
            Func<long, long> lineStart = l => l >= lines ? length : ed.Send(Sci.SCI_POSITIONFROMLINE, l);
            long a = ed.LineFromPosition(caret), b = a + 1;      // righe [a, b)
            s = lineStart(a); e = lineStart(b);
            long used = ed.Send(Sci.SCI_COUNTCHARACTERS, s, e);
            if (used > max)
            {
                long ls = s, le = e;
                long p = ed.Send(Sci.SCI_POSITIONRELATIVE, caret, -(max / 2));
                s = p > ls ? p : ls;                             // 0 = fuori dal documento
                p = ed.Send(Sci.SCI_POSITIONRELATIVE, s, max);
                e = p > s && p < le ? p : le;
                if (e == le)
                {
                    // Vicino alla fine della riga: lo spazio avanzato si usa prima del cursore
                    p = ed.Send(Sci.SCI_POSITIONRELATIVE, le, -max);
                    s = p > ls ? p : ls;
                }
                return;
            }
            bool up = a > 0, down = b < lines;
            while (up || down)
            {
                if (up)
                {
                    long ns = lineStart(a - 1), n = ed.Send(Sci.SCI_COUNTCHARACTERS, ns, s);
                    if (used + n <= max) { used += n; s = ns; a--; up = a > 0; } else up = false;
                }
                if (down)
                {
                    long ne = lineStart(b + 1), n = ed.Send(Sci.SCI_COUNTCHARACTERS, e, ne);
                    if (used + n <= max) { used += n; e = ne; b++; down = b < lines; } else down = false;
                }
            }
        }

        // Dimensione approssimata di una scheda in caratteri (byte: per eccesso), senza leggerla
        static long DocSize(Doc d)
        {
            try
            {
                if (d.Loaded) return d.Editor.Length;
                if (d.Loading) return 0;
                string file = d.BackupId != null && File.Exists(d.BackupFile) ? d.BackupFile : d.Path;
                return file != null && File.Exists(file) ? new FileInfo(file).Length : 0;
            }
            catch { return 0; }
        }

        static void AppendDoc(StringBuilder sb, Doc d, string text, string part, bool truncated)
        {
            sb.Append("\n<document name=\"").Append(d.Title.Replace("\"", "'")).Append("\"");
            if (part != null) sb.Append(" part=\"").Append(part).Append("\"");
            if (truncated) sb.Append(" truncated=\"yes\"");
            sb.Append(">\n").Append(text).Append("\n</document>\n");
        }

        // Testo di una scheda, al massimo maxChars caratteri: dall'editor se è caricata, altrimenti dalla copia di
        // sicurezza (testo non salvato) o dal file. null se non si può leggere (in caricamento, file sparito).
        string DocText(Doc d, int maxChars, out bool truncated)
        {
            truncated = false;
            if (d.Loading || maxChars <= 0) return null;
            string t;
            try
            {
                if (d.Loaded)
                {
                    var ed = d.Editor;
                    long len = ed.Length, bytes = Math.Min(len, (long)maxChars * 4);
                    t = ed.GetTextRange(0, bytes);
                    truncated = bytes < len;
                }
                else
                {
                    bool backup = d.BackupId != null && File.Exists(d.BackupFile);
                    string file = backup ? d.BackupFile : d.Path;
                    if (file == null) return "";
                    if (!File.Exists(file)) return null;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long flen = fs.Length;
                        int n = (int)Math.Min(flen, (long)maxChars * 4 + 4);
                        var buf = new byte[n];
                        int got = 0;
                        while (got < n) { int r = fs.Read(buf, got, n - got); if (r <= 0) break; got += r; }
                        int bom;
                        var enc = TextFile.Detect(buf, got, out bom);
                        if (!backup) enc = d.Encoding;
                        t = TextFile.GetEncoding(enc).GetString(buf, bom, got - bom);
                        truncated = got < flen;
                    }
                }
            }
            catch { return null; }
            if (t.Length > maxChars) { t = t.Substring(0, maxChars); truncated = true; }
            return t;
        }

        void FlushChat()
        {
            var job = aiChat;
            if (job == null) { aiChatTimer.Stop(); return; }
            string s;
            lock (job.Pending)
            {
                if (job.Pending.Length == 0) return;
                s = job.Pending.ToString();
                job.Pending.Clear();
            }
            if (!job.SeenText)
            {
                s = s.TrimStart();
                if (s.Length == 0) return;
                job.SeenText = true;
                ShowAiStatus(L.F("{0} sta rispondendo… Esc per fermare", "{0} is answering… Press Esc to stop", job.Provider.Name));
            }
            job.Answer.Append(s);
            AppendChat(s.Replace("\r\n", "\n").Replace('\r', '\n'), ChatColor.Normal, false);
        }

        void FinishChat(ChatJob job, AiResult res)
        {
            if (aiChat != job) return;
            FlushChat();
            aiChat = null;
            aiChatTimer.Stop();
            ShowAiStatus(null);
            string answer = job.Answer.ToString().TrimEnd();
            if (res.Ok && answer.Length > 0)
            {
                aiHistory.Add(new AiMessage("user", job.Question));
                aiHistory.Add(new AiMessage("assistant", answer));
                aiLastAnswer = answer;
                if (res.Truncated) AppendChat("\n" + L.T("(risposta interrotta perché troppo lunga)", "(answer cut off because it was too long)"), ChatColor.Dim, false);
            }
            else if (res.Cancelled) AppendChat((answer.Length > 0 ? "\n" : "") + L.T("(fermato)", "(stopped)"), ChatColor.Dim, false);
            else AppendChat((answer.Length > 0 ? "\n" : "") + (res.Error ?? L.F("{0} non ha scritto nulla.", "{0} wrote nothing.", job.Provider.Name)), ChatColor.Error, false);
            UpdateChatButtons();
            if (aiPanel.Visible && aiPanel.ContainsFocus) aiInput.Focus();
        }

        void StopChat()
        {
            if (aiChat != null) aiChat.Cts.Cancel();
        }

        // ---- La risposta nel documento ----

        void UseAnswer(AiOutput where)
        {
            string a = aiLastAnswer;
            if (string.IsNullOrEmpty(a) || aiJob != null) return;
            if (where == AiOutput.NewTab)
            {
                NewDoc();
                if (Ed == null) return;
            }
            var ed = Ed;
            var d = active;
            if (ed == null || d == null) return;
            if (ed.ReadOnly) { AiMessage(L.T("Il documento è di sola lettura.", "The document is read-only."), MessageBoxIcon.Information); return; }

            string eol = d.Eol == Sci.SC_EOL_CRLF ? "\r\n" : d.Eol == Sci.SC_EOL_CR ? "\r" : "\n";
            string text = a.Replace("\r\n", "\n").Replace('\r', '\n');
            if (eol != "\n") text = text.Replace("\n", eol);

            long s = ed.SelectionStart, e = ed.SelectionEnd;
            if (where == AiOutput.Replace && s == e)
            {
                AiMessage(L.T("Seleziona prima il testo da sostituire.", "Select the text to replace first."), MessageBoxIcon.Information);
                return;
            }
            if (where == AiOutput.Insert)
            {
                s = e = ed.CurrentPos;
                // In fondo a una riga con del testo la risposta va sulla riga dopo, non attaccata
                long line = ed.LineFromPosition(s);
                long lineStart = ed.Send(Sci.SCI_POSITIONFROMLINE, line), lineEnd = ed.Send(Sci.SCI_GETLINEENDPOSITION, line);
                if (s == lineEnd && s > lineStart) text = eol + text;
            }
            else if (e > s)
            {
                // La selezione finiva con un a capo che la risposta non ha: lo si rimette
                string old = ed.GetTextRange(Math.Max(s, e - 2), e);
                if ((old.EndsWith("\n") || old.EndsWith("\r")) && !text.EndsWith(eol)) text += eol;
            }
            ed.Send(Sci.SCI_BEGINUNDOACTION);
            if (e > s) ed.Send(Sci.SCI_DELETERANGE, s, e - s);
            ed.SendStr(Sci.SCI_INSERTTEXT, s, text);
            ed.Send(Sci.SCI_ENDUNDOACTION);
            long end = s + Encoding.UTF8.GetByteCount(text);
            // Il testo inserito resta selezionato (Ctrl+Z lo toglie)
            ed.Send(Sci.SCI_SETSEL, where == AiOutput.NewTab ? 0 : end, where == AiOutput.NewTab ? 0 : s);
            ed.Send(Sci.SCI_SCROLLCARET);
            ed.Focus();
            UpdateStatus();
        }
    }
}
