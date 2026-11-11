// Impostazioni dell'assistente: accensione, servizio (locale, Claude, Claude Code, compatibile OpenAI), download
// dell'assistente locale, chiavi API, prova della connessione. Le modifiche valgono con OK; Annulla
// rimette tutto com'era (la prova scrive le impostazioni in memoria, senza salvarle).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppPad
{
    class AiSettingsForm : Form
    {
        // Uno solo per tutta l'applicazione: un download fermato deve finire prima che ne parta un altro
        static readonly AiDownloader dl = new AiDownloader();

        static readonly string[] SettingKeys =
        {
            "ai.enabled", "ai.provider",
            "ai.claude.key", "ai.claude.model", "ai.claude.effort",
            "ai.claudecode.model",
            "ai.openai.url", "ai.openai.key", "ai.openai.model",
            "ai.local.model", "ai.local.variant", "ai.local.crashes"
        };

        const int InputWidth = 380;
        static readonly Color ErrColor = Color.FromArgb(185, 28, 28);
        static readonly Color OkColor = Color.FromArgb(21, 128, 61);
        static readonly Color Grey = Color.DimGray;
        static readonly Color Accent = Color.FromArgb(37, 99, 235);

        readonly Dictionary<string, string> snapshot = new Dictionary<string, string>();

        CheckBox chkOn;
        RadioButton rLocal, rClaude, rClaudeCode, rOpenAi, rCpu, rGpu;
        GroupBox gLocal, gClaude, gClaudeCode, gOpenAi;
        ComboBox cbModel, cbClaudeModel, cbEffort, cbCcModel, cbUrl, cbOaModel;
        Label lblModelNote, lblLocalState, lblTest;
        ProgressBar bar;
        Button btnDownload, btnRemove, btnList, btnTest, btnSpeed;
        Label lblSpeed;
        CancellationTokenSource speedCts;
        bool speedAfterDownload;
        TextBox txClaudeKey, txOaKey;

        string customGguf;          // file .gguf scelto dall'utente
        int modelIndex = -1;        // selezione precedente (se "Scegli un file…" viene annullato)
        bool filling, downloading, testedLocal;
        CancellationTokenSource testCts;

        class Item
        {
            public string Id, Text;
            public Item(string id, string text) { Id = id; Text = text; }
            public override string ToString() { return Text; }
        }

        public static void ShowFor(IWin32Window owner)
        {
            using (var f = new AiSettingsForm()) f.ShowDialog(owner);
        }

        AiSettingsForm()
        {
            foreach (var k in SettingKeys) snapshot[k] = Settings.Get(k, (string)null);

            Text = L.T("Impostazioni dell'assistente", "Assistant settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(0, 0, 12, 12);

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(12, 10) };
            Controls.Add(root);

            chkOn = new CheckBox { Text = L.T("&Attiva l'assistente", "&Turn on the assistant"), AutoSize = true, Margin = new Padding(3, 3, 3, 8) };
            root.Controls.Add(chkOn);

            // ---- Servizio ----
            var gService = NewGroup(L.T("Servizio", "Service"));
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(8, 20), WrapContents = false, Margin = new Padding(0) };
            rLocal = new RadioButton { Text = L.T("Assistente &locale: gira sul PC, il testo non esce dal computer", "&Local assistant: runs on this PC, the text never leaves the computer"), AutoSize = true };
            rClaude = new RadioButton { Text = L.T("&Claude (Anthropic), con una chiave API", "&Claude (Anthropic), with an API key"), AutoSize = true };
            rClaudeCode = new RadioButton { Text = L.T("Claude Code, con il tuo abbonamento Claude (solo uso personale)", "Claude Code, with your Claude subscription (personal use only)"), AutoSize = true };
            rOpenAi = new RadioButton { Text = L.T("Altro servizio compatibile &OpenAI (OpenAI, Qwen, OpenRouter, Ollama…)", "Other &OpenAI-compatible service (OpenAI, Qwen, OpenRouter, Ollama…)"), AutoSize = true };
            flow.Controls.AddRange(new Control[] { rLocal, rClaude, rClaudeCode, rOpenAi });
            gService.Controls.Add(flow);
            root.Controls.Add(gService);

            BuildLocal(root);
            BuildClaude(root);
            BuildClaudeCode(root);
            BuildOpenAi(root);

            // ---- Prova e pulsanti ----
            var bottom = Grid();
            btnTest = new Button { Text = L.T("&Prova connessione", "Test co&nnection"), AutoSize = true, Margin = new Padding(3, 3, 8, 3) };
            lblTest = new Label { AutoSize = true, MaximumSize = new Size(InputWidth, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            bottom.Controls.Add(btnTest, 0, 0);
            bottom.Controls.Add(lblTest, 1, 0);
            root.Controls.Add(bottom);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
            var cancel = new Button { Text = L.T("Annulla", "Cancel"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            var ok = new Button { Text = "OK", Size = new Size(88, 28) };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            root.Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;

            // ---- Valori ----
            LoadValues();

            rLocal.CheckedChanged += delegate { UpdateProvider(); };
            rClaude.CheckedChanged += delegate { UpdateProvider(); };
            rClaudeCode.CheckedChanged += delegate { UpdateProvider(); };
            rOpenAi.CheckedChanged += delegate { UpdateProvider(); };
            chkOn.CheckedChanged += delegate { UpdateProvider(); };
            btnTest.Click += delegate { Test(); };
            ok.Click += delegate { Ok(); };
            dl.Progress += OnProgress;
            FormClosing += OnClosing;
            FormClosed += delegate { dl.Progress -= OnProgress; };
            UpdateProvider();
        }

        // ---- Costruzione ----

        static GroupBox NewGroup(string title)
        {
            return new GroupBox
            {
                Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 4, 6, 0), Margin = new Padding(0, 0, 0, 8),
                MinimumSize = new Size(InputWidth + 140, 0)
            };
        }

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        static GroupBox Group(string title, out TableLayoutPanel t)
        {
            var g = NewGroup(title);
            t = Grid();
            t.Location = new Point(8, 20);
            g.Controls.Add(t);
            return g;
        }

        // Una riga: etichetta (o niente) e controllo
        static void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount;
            t.RowCount = r + 1;
            if (label != null)
                t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) }, 0, r);
            t.Controls.Add(c, 1, r);
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(InputWidth, 0), ForeColor = Grey, Margin = new Padding(3, 0, 3, 6) };
        }

        static FlowLayoutPanel Line(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(cs);
            return f;
        }

        void BuildLocal(TableLayoutPanel root)
        {
            TableLayoutPanel t;
            gLocal = Group(L.T("Assistente locale", "Local assistant"), out t);
            cbModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = InputWidth };
            Row(t, L.T("&Modello:", "&Model:"), cbModel);
            lblModelNote = Note("");
            Row(t, null, lblModelNote);
            rCpu = new RadioButton { Text = L.T("Processore (CPU)", "Processor (CPU)"), AutoSize = true, Checked = true };
            rGpu = new RadioButton { Text = L.T("Scheda grafica (GPU, Vulkan)", "Graphics card (GPU, Vulkan)"), AutoSize = true };
            btnSpeed = new Button { Text = L.T("Misura la &velocità", "Measure &speed"), AutoSize = true, MinimumSize = new Size(88, 26), Margin = new Padding(12, 2, 3, 3) };
            Row(t, L.T("Esecuzione:", "Runs on:"), Line(rCpu, rGpu, btnSpeed));
            lblSpeed = Note(L.T("La GPU può essere più veloce, ma non sempre: \"Misura la velocità\" prova processore e GPU e sceglie la GPU solo se è almeno del 30% più veloce.",
                                "The GPU can be faster, but not always: \"Measure speed\" tries the processor and the GPU and picks the GPU only if it is at least 30% faster."));
            Row(t, null, lblSpeed);
            lblLocalState = new Label { AutoSize = true, MaximumSize = new Size(InputWidth, 0), Margin = new Padding(3, 7, 3, 3) };
            Row(t, L.T("Stato:", "Status:"), lblLocalState);
            bar = new ProgressBar { Width = InputWidth, Height = 16, Maximum = 1000, Visible = false, Margin = new Padding(3, 4, 3, 4) };
            Row(t, null, bar);
            btnDownload = new Button { Text = L.T("&Scarica", "&Download"), AutoSize = true, MinimumSize = new Size(88, 28) };
            btnRemove = new Button { Text = L.T("Cancella i &file…", "Delete the &files…"), AutoSize = true, MinimumSize = new Size(88, 28) };
            Row(t, null, Line(btnDownload, btnRemove));

            cbModel.SelectedIndexChanged += delegate { ModelChanged(); };
            rCpu.CheckedChanged += delegate { UpdateLocal(); };
            btnSpeed.Click += delegate { Speed(); };
            btnDownload.Click += delegate { if (downloading) { dl.Cancel(); btnDownload.Enabled = false; } else StartDownload(); };
            btnRemove.Click += delegate { RemoveFiles(); };
            root.Controls.Add(gLocal);
        }

        void BuildClaude(TableLayoutPanel root)
        {
            TableLayoutPanel t;
            gClaude = Group("Claude", out t);
            txClaudeKey = new TextBox { Width = InputWidth - 80, UseSystemPasswordChar = true };
            var show = new CheckBox { Text = L.T("Mostra", "Show"), AutoSize = true, Margin = new Padding(8, 5, 3, 3) };
            show.CheckedChanged += delegate { txClaudeKey.UseSystemPasswordChar = !show.Checked; };
            Row(t, L.T("C&hiave API:", "API &key:"), Line(txClaudeKey, show));
            var link = new LinkLabel { Text = L.T("Crea una chiave nella console di Anthropic", "Create a key in the Anthropic console"), AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(3, 0, 3, 6) };
            link.LinkClicked += delegate { Util.OpenUrl("https://console.anthropic.com/settings/keys"); };
            Row(t, null, link);
            cbClaudeModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = InputWidth };
            cbClaudeModel.Items.AddRange(ClaudeProvider.Models);
            Row(t, L.T("Mo&dello:", "Mo&del:"), cbClaudeModel);
            cbEffort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = InputWidth };
            cbEffort.Items.AddRange(new object[]
            {
                new Item("", L.T("Predefinito del modello", "Model default")),
                new Item("low", L.T("Basso: più veloce ed economico", "Low: faster and cheaper")),
                new Item("medium", L.T("Medio", "Medium")),
                new Item("high", L.T("Alto: più accurato", "High: more thorough"))
            });
            Row(t, L.T("&Impegno:", "&Effort:"), cbEffort);
            Row(t, null, Note(L.T("Il testo su cui lavora l'assistente viene inviato ad Anthropic. L'uso si paga a consumo sul tuo account. La chiave resta cifrata sul PC.",
                                  "The text the assistant works on is sent to Anthropic. Usage is billed to your account. The key is stored encrypted on this PC.")));
            root.Controls.Add(gClaude);
        }

        void BuildClaudeCode(TableLayoutPanel root)
        {
            TableLayoutPanel t;
            gClaudeCode = Group("Claude Code", out t);
            bool installed = ClaudeCodeProvider.FindExe() != null;
            var state = Note(installed
                ? L.T("Installato. Usa il login che hai fatto in Claude Code, senza chiave API.", "Installed. It uses the login you made in Claude Code, no API key needed.")
                : L.T("Claude Code non è installato su questo PC: installalo e fai il login con il tuo account Claude.", "Claude Code is not installed on this PC: install it and log in with your Claude account."));
            state.ForeColor = installed ? OkColor : ErrColor;
            state.Margin = new Padding(3, 3, 3, 6);
            Row(t, null, state);
            if (!installed)
            {
                var link = new LinkLabel { Text = L.T("Come installare Claude Code", "How to install Claude Code"), AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(3, 0, 3, 6) };
                link.LinkClicked += delegate { Util.OpenUrl("https://docs.claude.com/en/docs/claude-code/setup"); };
                Row(t, null, link);
            }
            cbCcModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = InputWidth };
            cbCcModel.Items.Add(new Item("", L.T("Predefinito di Claude Code", "Claude Code default")));
            foreach (var m in ClaudeCodeProvider.Models) cbCcModel.Items.Add(new Item(m, m));
            Row(t, L.T("Mo&dello:", "Mo&del:"), cbCcModel);
            Row(t, null, Note(L.T("AppPad avvia Claude Code senza strumenti (non può leggere file né eseguire comandi) e senza salvare la sessione: riceve solo il testo su cui lavora l'assistente, che viene inviato ad Anthropic. L'uso rientra nei limiti del tuo abbonamento.",
                                  "AppPad runs Claude Code without tools (it cannot read files or run commands) and without saving the session: it only receives the text the assistant works on, which is sent to Anthropic. Usage counts toward your subscription limits.")));
            root.Controls.Add(gClaudeCode);
        }

        void BuildOpenAi(TableLayoutPanel root)
        {
            TableLayoutPanel t;
            gOpenAi = Group(L.T("Servizio compatibile OpenAI", "OpenAI-compatible service"), out t);
            cbUrl = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = InputWidth };
            cbUrl.Items.AddRange(new object[]
            {
                new Item("https://api.openai.com/v1", "OpenAI — https://api.openai.com/v1"),
                new Item("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "Qwen (Alibaba Cloud) — https://dashscope-intl.aliyuncs.com/compatible-mode/v1"),
                new Item("https://openrouter.ai/api/v1", "OpenRouter — https://openrouter.ai/api/v1"),
                new Item("http://localhost:11434/v1", L.T("Ollama sul PC — http://localhost:11434/v1", "Ollama on this PC — http://localhost:11434/v1")),
                new Item("http://localhost:1234/v1", L.T("LM Studio sul PC — http://localhost:1234/v1", "LM Studio on this PC — http://localhost:1234/v1"))
            });
            cbUrl.DropDownWidth = InputWidth + 100;
            // Dall'elenco si prende solo l'indirizzo
            cbUrl.SelectionChangeCommitted += delegate
            {
                var it = cbUrl.SelectedItem as Item;
                if (it != null) BeginInvoke((Action)delegate { cbUrl.Text = it.Id; cbUrl.SelectionStart = cbUrl.Text.Length; });
            };
            Row(t, L.T("I&ndirizzo:", "Add&ress:"), cbUrl);
            txOaKey = new TextBox { Width = InputWidth - 80, UseSystemPasswordChar = true };
            var show = new CheckBox { Text = L.T("Mostra", "Show"), AutoSize = true, Margin = new Padding(8, 5, 3, 3) };
            show.CheckedChanged += delegate { txOaKey.UseSystemPasswordChar = !show.Checked; };
            Row(t, L.T("C&hiave API:", "API &key:"), Line(txOaKey, show));
            Row(t, null, Note(L.T("Non serve per i programmi sul PC come Ollama e LM Studio.", "Not needed for programs on this PC such as Ollama and LM Studio.")));
            cbOaModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = InputWidth - 96 };
            btnList = new Button { Text = L.T("&Elenca", "L&ist"), AutoSize = true, MinimumSize = new Size(88, 26), Margin = new Padding(8, 2, 3, 3) };
            btnList.Click += delegate { ListModels(); };
            Row(t, L.T("Mo&dello:", "Mo&del:"), Line(cbOaModel, btnList));
            Row(t, null, Note(L.T("Il testo su cui lavora l'assistente viene inviato a questo indirizzo; resta sul PC solo se il servizio gira sul PC. La chiave resta cifrata sul PC.",
                                  "The text the assistant works on is sent to this address; it stays on this PC only if the service runs on this PC. The key is stored encrypted on this PC.")));
            root.Controls.Add(gOpenAi);
        }

        // ---- Valori ----

        void LoadValues()
        {
            chkOn.Checked = AiSettings.Enabled;
            string p = AiSettings.Provider;
            (p == AiSettings.Claude ? rClaude : p == AiSettings.ClaudeCode ? rClaudeCode : p == AiSettings.OpenAi ? rOpenAi : rLocal).Checked = true;

            string lm = AiSettings.LocalModel;
            if (lm.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) customGguf = lm;
            FillModels(lm);
            (AiSettings.LocalVariant == "vulkan" ? rGpu : rCpu).Checked = true;

            txClaudeKey.Text = AiSettings.ClaudeKey;
            cbClaudeModel.Text = AiSettings.ClaudeModel;
            string effort = AiSettings.ClaudeEffort;
            cbEffort.SelectedIndex = Math.Max(0, cbEffort.Items.Cast<Item>().ToList().FindIndex(i => i.Id == effort));

            string ccm = AiSettings.ClaudeCodeModel;
            int ci = cbCcModel.Items.Cast<Item>().ToList().FindIndex(i => i.Id == ccm);
            if (ci < 0) { cbCcModel.Items.Add(new Item(ccm, ccm)); ci = cbCcModel.Items.Count - 1; }   // nome completo scritto a mano in settings.txt
            cbCcModel.SelectedIndex = ci;

            cbUrl.Text = AiSettings.OpenAiUrl;
            txOaKey.Text = AiSettings.OpenAiKey;
            cbOaModel.Text = AiSettings.OpenAiModel;
        }

        // Scrive i valori della finestra nelle impostazioni (in memoria: le salva solo OK)
        void Apply()
        {
            // Si scrive solo ciò che è cambiato: i valori mai toccati restano predefiniti (e seguono le versioni future)
            if (AiSettings.Enabled != chkOn.Checked) AiSettings.Enabled = chkOn.Checked;
            string provider = rClaude.Checked ? AiSettings.Claude : rClaudeCode.Checked ? AiSettings.ClaudeCode : rOpenAi.Checked ? AiSettings.OpenAi : AiSettings.Local;
            if (AiSettings.Provider != provider) AiSettings.Provider = provider;
            string id = SelectedModelId();
            if (id.Length > 0 && AiSettings.LocalModel != id) AiSettings.LocalModel = id;
            if (AiSettings.LocalVariant != Variant()) AiSettings.LocalVariant = Variant();
            if (AiSettings.ClaudeKey != txClaudeKey.Text.Trim()) AiSettings.ClaudeKey = txClaudeKey.Text.Trim();
            string model = cbClaudeModel.Text.Trim().Length > 0 ? cbClaudeModel.Text.Trim() : ClaudeProvider.DefaultModel;
            if (AiSettings.ClaudeModel != model) AiSettings.ClaudeModel = model;
            var e = cbEffort.SelectedItem as Item;
            string effort = e != null ? e.Id : "";
            if (AiSettings.ClaudeEffort != effort) AiSettings.ClaudeEffort = effort;
            var cc = cbCcModel.SelectedItem as Item;
            string ccModel = cc != null ? cc.Id : "";
            if (AiSettings.ClaudeCodeModel != ccModel) AiSettings.ClaudeCodeModel = ccModel;
            if (AiSettings.OpenAiUrl != cbUrl.Text.Trim()) AiSettings.OpenAiUrl = cbUrl.Text.Trim();
            if (AiSettings.OpenAiKey != txOaKey.Text.Trim()) AiSettings.OpenAiKey = txOaKey.Text.Trim();
            if (AiSettings.OpenAiModel != cbOaModel.Text.Trim()) AiSettings.OpenAiModel = cbOaModel.Text.Trim();
            // Scegliere di nuovo l'assistente locale lo riattiva anche se si era spento dopo troppe chiusure improvvise
            if (rLocal.Checked && Settings.Get("ai.local.crashes", 0) > 0) Settings.Set("ai.local.crashes", 0);
        }

        void Restore()
        {
            foreach (var kv in snapshot)
            {
                if (kv.Value == null) Settings.Remove(kv.Key);
                else Settings.Set(kv.Key, kv.Value);
            }
        }

        // Motivo per cui la configurazione del servizio scelto non va, o null
        string Invalid()
        {
            if (rClaude.Checked)
            {
                if (txClaudeKey.Text.Trim().Length == 0) { txClaudeKey.Focus(); return L.T("Inserisci la chiave API di Claude.", "Enter the Claude API key."); }
            }
            else if (rClaudeCode.Checked)
            {
                if (ClaudeCodeProvider.FindExe() == null) return L.T("Claude Code non è installato su questo PC.", "Claude Code is not installed on this PC.");
            }
            else if (rOpenAi.Checked)
            {
                Uri u;
                if (!Uri.TryCreate(cbUrl.Text.Trim(), UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                {
                    cbUrl.Focus();
                    return L.T("L'indirizzo del servizio non è valido (esempio: https://api.openai.com/v1).", "The service address is not valid (example: https://api.openai.com/v1).");
                }
                if (cbOaModel.Text.Trim().Length == 0)
                {
                    cbOaModel.Focus();
                    return L.T("Indica il modello da usare (con Elenca vedi quelli disponibili).", "Enter the model to use (List shows the available ones).");
                }
            }
            return null;
        }

        void UpdateProvider()
        {
            SuspendLayout();
            gLocal.Visible = rLocal.Checked;
            gClaude.Visible = rClaude.Checked;
            gClaudeCode.Visible = rClaudeCode.Checked;
            gOpenAi.Visible = rOpenAi.Checked;
            ResumeLayout();
            if (testCts == null) lblTest.Text = chkOn.Checked ? "" : L.T("L'assistente è spento: attivalo per usarlo dal menu Assistente.", "The assistant is off: turn it on to use it from the Assistant menu.");
            lblTest.ForeColor = Grey;
            UpdateLocal();
        }

        // ---- Assistente locale ----

        string SelectedModelId()
        {
            var it = cbModel.SelectedItem as Item;
            return it != null ? it.Id : "";
        }

        AiModel SelectedCatalogModel() { return AiCatalog.Find(SelectedModelId()); }

        string Variant() { return rGpu.Checked ? "vulkan" : "cpu"; }

        void FillModels(string select)
        {
            filling = true;
            cbModel.Items.Clear();
            var rec = AiCatalog.Recommended();
            foreach (var m in AiCatalog.Models.Reverse())
            {
                string t = m.Name + " — " + Util.FormatBytes(m.Size);
                if (AiCatalog.Unsuitable(m) != null) t += L.T(", troppo grande per questo PC", ", too large for this PC");
                else if (m == rec) t += L.T(", consigliato per questo PC", ", recommended for this PC");
                else if (m.Size < rec.Size) t += L.T(", più leggero e meno preciso", ", lighter and less accurate");
                cbModel.Items.Add(new Item(m.Id, t));
            }
            if (customGguf != null) cbModel.Items.Add(new Item(customGguf, L.F("File tuo: {0}", "Your file: {0}", Path.GetFileName(customGguf))));
            cbModel.Items.Add(new Item("", L.T("Scegli un file .gguf…", "Choose a .gguf file…")));
            int i = cbModel.Items.Cast<Item>().ToList().FindIndex(x => x.Id.Length > 0 && string.Equals(x.Id, select, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = cbModel.Items.Cast<Item>().ToList().FindIndex(x => x.Id == rec.Id);
            cbModel.SelectedIndex = i;
            modelIndex = i;
            filling = false;
        }

        void ModelChanged()
        {
            if (filling) return;
            if (SelectedModelId().Length == 0)
            {
                using (var d = new OpenFileDialog
                {
                    Title = L.T("Modello per l'assistente locale", "Model for the local assistant"),
                    Filter = L.T("Modelli GGUF (*.gguf)|*.gguf", "GGUF models (*.gguf)|*.gguf")
                })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        customGguf = d.FileName;
                        FillModels(customGguf);
                    }
                    else
                    {
                        filling = true; cbModel.SelectedIndex = modelIndex; filling = false;
                    }
                }
            }
            modelIndex = cbModel.SelectedIndex;
            UpdateLocal();
        }

        bool LlamaReady()
        {
            return AiDownloader.LlamaReady(Variant());
        }

        // Byte ancora da scaricare per il modello e la variante scelti
        long NeededBytes()
        {
            var m = SelectedCatalogModel();
            long need = 0;
            if (m != null && AiDownloader.ModelPath(m) == null) need += m.Size;
            if (!LlamaReady()) need += AiDownloader.LlamaSize(Variant());
            return need;
        }

        void UpdateLocal()
        {
            if (!rLocal.Checked) return;
            var m = SelectedCatalogModel();
            string id = SelectedModelId();
            string unsuitable = m != null ? AiCatalog.Unsuitable(m) : null;
            bool fileMissing = m == null && !File.Exists(id);

            if (m != null)
            {
                lblModelNote.Text = unsuitable ?? L.F("{0} di Alibaba, licenza {1}, scaricato da Hugging Face.", "{0} by Alibaba, {1} license, downloaded from Hugging Face.", m.Name, m.License);
                lblModelNote.ForeColor = unsuitable != null ? ErrColor : Grey;
            }
            else
            {
                lblModelNote.Text = fileMissing ? L.T("File non trovato.", "File not found.")
                    : L.T("Un modello di chat in formato GGUF, per esempio da Hugging Face.", "A chat model in GGUF format, for example from Hugging Face.");
                lblModelNote.ForeColor = fileMissing ? ErrColor : Grey;
            }

            cbModel.Enabled = rCpu.Enabled = rGpu.Enabled = !downloading;
            btnDownload.Text = downloading ? L.T("&Ferma", "&Stop") : L.T("&Scarica", "&Download");
            if (downloading) { btnDownload.Enabled = true; btnRemove.Enabled = false; return; }

            long need = NeededBytes(), used = AiDownloader.UsedBytes();
            btnDownload.Enabled = need > 0 && unsuitable == null && !fileMissing;
            btnRemove.Enabled = used > 0;
            string s;
            if (fileMissing) s = L.T("Scegli un modello.", "Choose a model.");
            else if (need == 0) s = L.F("Pronto. Spazio occupato sul disco: {0}.", "Ready. Disk space used: {0}.", Util.FormatBytes(used));
            else s = L.F("Da scaricare: {0}. Spazio occupato ora: {1}.", "To download: {0}. Disk space used now: {1}.", Util.FormatBytes(need), Util.FormatBytes(used));
            if (LocalAi.Crashes >= LocalAi.MaxCrashes)
                s += "\n" + L.T("Si era spento dopo alcune chiusure improvvise: con OK si riattiva.", "It was turned off after closing unexpectedly: OK turns it back on.");
            lblLocalState.Text = s;
            lblLocalState.ForeColor = need == 0 && !fileMissing ? OkColor : ForeColor;
        }

        void StartDownload()
        {
            if (dl.Busy)
            {
                lblLocalState.Text = L.T("Il download precedente si sta fermando: riprova tra qualche secondo.", "The previous download is stopping: try again in a few seconds.");
                lblLocalState.ForeColor = ForeColor;
                return;
            }
            LocalAi.Instance.Stop();    // i file di llama.cpp in uso non si possono sostituire
            downloading = true;
            bar.Style = ProgressBarStyle.Marquee;
            bar.Visible = true;
            lblLocalState.Text = L.T("Avvio del download…", "Starting the download…");
            lblLocalState.ForeColor = ForeColor;
            UpdateLocal();
            dl.Start(SelectedCatalogModel(), Variant());
        }

        void OnProgress(AiDownloadStatus s)
        {
            Post(delegate { ShowProgress(s); });
        }

        void ShowProgress(AiDownloadStatus s)
        {
            if (!downloading) return;
            if (s.Finished)
            {
                downloading = false;
                bar.Visible = false;
                UpdateLocal();
                if (s.Error != null) { lblLocalState.Text = s.Error; lblLocalState.ForeColor = ErrColor; speedAfterDownload = false; }
                else if (speedAfterDownload) { speedAfterDownload = false; Speed(); }
                return;
            }
            if (s.Total > 0)
            {
                bar.Style = ProgressBarStyle.Continuous;
                bar.Value = (int)Math.Min(1000, s.Done * 1000 / s.Total);
                lblLocalState.Text = s.Text + "\n" + L.F("{0} di {1}", "{0} of {1}", Util.FormatBytes(s.Done), Util.FormatBytes(s.Total));
            }
            else
            {
                bar.Style = ProgressBarStyle.Marquee;
                lblLocalState.Text = s.Text;
            }
            lblLocalState.ForeColor = ForeColor;
        }

        void RemoveFiles()
        {
            long used = AiDownloader.UsedBytes();
            if (MessageBox.Show(this,
                    L.F("Cancellare i file dell'assistente locale ({0})? Per usarlo di nuovo andranno riscaricati.",
                        "Delete the local assistant files ({0})? They will need to be downloaded again to use it.", Util.FormatBytes(used))
                    + (customGguf != null ? "\n" + L.T("Il tuo file .gguf non viene toccato.", "Your .gguf file is not touched.") : ""),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            LocalAi.Instance.Stop();
            AiDownloader.RemoveAll();
            UpdateLocal();
            if (AiDownloader.UsedBytes() > 0)
            {
                lblLocalState.Text = L.T("Alcuni file sono in uso e non si sono potuti cancellare: riprova dopo aver chiuso AppPad.",
                                         "Some files are in use and could not be deleted: try again after closing AppPad.");
                lblLocalState.ForeColor = ErrColor;
            }
        }

        // ---- Prova di velocità: processore o GPU ----

        void Speed()
        {
            if (speedCts != null) { speedCts.Cancel(); btnSpeed.Enabled = false; return; }
            if (downloading || testCts != null) return;
            var m = SelectedCatalogModel();
            if (m == null && !File.Exists(SelectedModelId())) return;
            bool modelMissing = m != null && AiDownloader.ModelPath(m) == null;
            bool engineMissing = !AiDownloader.LlamaReady("vulkan");
            if (modelMissing || engineMissing)
            {
                if (m != null && AiCatalog.Unsuitable(m) != null) return;
                long need = (modelMissing ? m.Size : 0) + (engineMissing ? AiDownloader.LlamaSize("vulkan") : 0);
                if (MessageBox.Show(this,
                        L.F("Per la prova serve scaricare {0} ({1}). Il motore per GPU fa girare anche il processore, quindi basta per entrambi.\n\nScaricare adesso?",
                            "The test needs a {0} download ({1}). The GPU engine also runs on the processor, so it covers both.\n\nDownload now?",
                            Util.FormatBytes(need),
                            modelMissing && engineMissing ? L.T("modello e motore per GPU", "model and GPU engine")
                            : modelMissing ? L.T("modello", "model") : L.T("motore per GPU", "GPU engine")),
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                rGpu.Checked = true;
                speedAfterDownload = true;
                StartDownload();
                return;
            }

            Apply();                // la prova usa il modello scelto qui
            testedLocal = true;
            var cts = speedCts = new CancellationTokenSource();
            btnSpeed.Text = L.T("&Ferma la prova", "&Stop the test");
            cbModel.Enabled = rCpu.Enabled = rGpu.Enabled = btnDownload.Enabled = btnRemove.Enabled = btnTest.Enabled = false;
            ShowSpeed(L.T("Prova sul processore…", "Testing the processor…"), Grey);

            var th = new Thread(delegate ()
            {
                double cpu = 0, gpu = 0;
                string cpuErr = null, gpuErr = null;
                try { cpu = LocalAi.Instance.MeasureSpeed(false, cts.Token); }
                catch (Exception ex) { cpuErr = FirstLine(ex.Message); }
                if (!cts.IsCancellationRequested)
                {
                    Post(delegate { if (speedCts == cts) ShowSpeed(L.T("Prova sulla GPU… la prima volta può richiedere un minuto.", "Testing the GPU… the first time it can take a minute."), Grey); });
                    try { gpu = LocalAi.Instance.MeasureSpeed(true, cts.Token); }
                    catch (Exception ex) { gpuErr = FirstLine(ex.Message); }
                }
                Post(delegate { SpeedDone(cts, cpu, gpu, cpuErr, gpuErr); });
            });
            th.IsBackground = true;
            th.Start();
        }

        static string FirstLine(string s)
        {
            s = (s ?? "").Trim();
            int i = s.IndexOf('\n');
            return i > 0 ? s.Substring(0, i).Trim() : s;
        }

        void ShowSpeed(string text, Color c)
        {
            lblSpeed.Text = text;
            lblSpeed.ForeColor = c;
        }

        void SpeedDone(CancellationTokenSource cts, double cpu, double gpu, string cpuErr, string gpuErr)
        {
            if (speedCts != cts) return;
            speedCts = null;
            btnSpeed.Enabled = btnTest.Enabled = true;
            btnSpeed.Text = L.T("Misura la &velocità", "Measure &speed");
            if (cts.IsCancellationRequested) { ShowSpeed(L.T("Prova fermata.", "Test stopped."), Grey); UpdateLocal(); return; }

            string cpuText = cpu.ToString("0.0"), gpuText = gpu.ToString("0.0");
            if (cpuErr != null && gpuErr != null)
                ShowSpeed(L.F("La prova non è riuscita: {0}", "The test failed: {0}", cpuErr), ErrColor);
            else if (gpuErr != null)
            {
                rCpu.Checked = true;
                ShowSpeed(L.F("Processore: {0} token/s. La GPU non funziona con l'assistente ({1}): resta il processore.",
                              "Processor: {0} tokens/s. The GPU does not work with the assistant ({1}): the processor stays.", cpuText, gpuErr), ErrColor);
            }
            else if (cpuErr != null)
            {
                rGpu.Checked = true;
                ShowSpeed(L.F("GPU: {0} token/s. Il processore non ha funzionato ({1}): scelta la GPU.",
                              "GPU: {0} tokens/s. The processor did not work ({1}): GPU chosen.", gpuText, cpuErr), ErrColor);
            }
            else
            {
                bool useGpu = gpu >= cpu * 1.3;
                (useGpu ? rGpu : rCpu).Checked = true;
                int pct = cpu > 0 ? (int)Math.Round((gpu / cpu - 1) * 100) : 0;
                ShowSpeed(L.F("Processore: {0} token/s · GPU: {1} token/s.", "Processor: {0} tokens/s · GPU: {1} tokens/s.", cpuText, gpuText) + " " +
                          (useGpu ? L.F("La GPU è più veloce del {0}%: scelta la GPU.", "The GPU is {0}% faster: GPU chosen.", pct)
                                  : L.T("La GPU non è abbastanza più veloce (serve almeno il 30%): resta il processore, che parte prima.",
                                        "The GPU is not fast enough (at least 30% is needed): the processor stays, as it starts sooner.")),
                          OkColor);
            }
            UpdateLocal();
        }

        // ---- Servizi in rete ----

        void ListModels()
        {
            string url = cbUrl.Text.Trim(), key = txOaKey.Text.Trim();
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) { ShowTest(Invalid() ?? "", ErrColor); return; }
            btnList.Enabled = false;
            ShowTest(L.T("Lettura dei modelli disponibili…", "Reading the available models…"), Grey);
            var th = new Thread(delegate ()
            {
                List<string> ids = null; string error = null;
                try { ids = new OpenAiProvider { BaseUrl = url, ApiKey = key }.ListModels(); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    btnList.Enabled = true;
                    if (error != null) { ShowTest(L.F("Elenco dei modelli non disponibile: {0}", "Model list not available: {0}", error), ErrColor); return; }
                    string current = cbOaModel.Text;
                    cbOaModel.Items.Clear();
                    cbOaModel.Items.AddRange(ids.Cast<object>().ToArray());
                    cbOaModel.Text = current;
                    if (ids.Count == 0) { ShowTest(L.T("Il servizio non ha restituito modelli.", "The service returned no models."), ErrColor); return; }
                    ShowTest(L.F("{0} modelli disponibili: sceglilo dall'elenco.", "{0} models available: choose one from the list.", ids.Count), Grey);
                    cbOaModel.Focus();
                    cbOaModel.DroppedDown = true;
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowTest(string text, Color c)
        {
            lblTest.Text = text;
            lblTest.ForeColor = c;
        }

        // Una domanda minima al servizio scelto, con le impostazioni della finestra
        void Test()
        {
            if (testCts != null) { testCts.Cancel(); btnTest.Enabled = false; return; }
            if (speedCts != null) return;
            string why = Invalid();
            if (why != null) { ShowTest(why, ErrColor); return; }
            Apply();
            IAiProvider p;
            if (rLocal.Checked)
            {
                if (downloading) { ShowTest(L.T("Attendi la fine del download.", "Wait for the download to finish."), ErrColor); return; }
                why = LocalAi.NotReadyReason();
                if (why != null) { ShowTest(why, ErrColor); return; }
                LocalAi.Instance.Stop();    // riparte con il modello e la variante scelti qui
                testedLocal = true;
                p = LocalAi.Instance;
            }
            else p = AiSettings.CloudProvider();
            if (p == null) return;

            var req = AiRequest.Simple("Reply with exactly the word OK and nothing else.", "Test");
            if (rClaude.Checked) req.MaxTokens = 64;
            var cts = testCts = new CancellationTokenSource();
            btnTest.Text = L.T("Ferma la &prova", "&Stop the test");
            ShowTest(L.F("Prova di {0} in corso…", "Testing {0}…", p.Name), Grey);
            var local = p as LocalAi;
            Action<string> onStatus = s => { if (s != null) Post(delegate { if (testCts == cts) ShowTest(s, Grey); }); };
            if (local != null) local.StatusChanged += onStatus;
            var sw = Stopwatch.StartNew();

            var th = new Thread(delegate ()
            {
                AiResult r;
                try { r = p.Run(req, delegate (string t) { }, cts.Token); }
                catch (Exception ex) { r = new AiResult { Error = ex.Message }; }
                if (local != null) local.StatusChanged -= onStatus;
                double secs = sw.Elapsed.TotalSeconds;
                Post(delegate
                {
                    testCts = null;
                    btnTest.Enabled = true;
                    btnTest.Text = L.T("&Prova connessione", "Test co&nnection");
                    if (r.Cancelled) ShowTest(L.T("Prova fermata.", "Test stopped."), Grey);
                    else if (r.Ok)
                        ShowTest(L.F("Funziona: {0} ha risposto in {1} s{2}.", "It works: {0} answered in {1} s{2}.",
                                     p.Name, secs.ToString("0.0"), string.IsNullOrEmpty(r.Model) || local != null ? "" : " (" + r.Model + ")"), OkColor);
                    else ShowTest(r.Error, ErrColor);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // Esegue sul thread della finestra, se è ancora aperta
        void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // ---- Chiusura ----

        void Ok()
        {
            if (speedCts != null)
            {
                MessageBox.Show(this, L.T("La prova di velocità è in corso: attendi che finisca, oppure fermala.", "The speed test is running: wait for it to finish, or stop it."),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (downloading)
            {
                MessageBox.Show(this, L.T("Il download è in corso: attendi che finisca, oppure fermalo.", "The download is in progress: wait for it to finish, or stop it."),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (chkOn.Checked)
            {
                string why = Invalid();
                if (why != null) { MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                long need = rLocal.Checked ? NeededBytes() : 0;
                var m = SelectedCatalogModel();
                if (need > 0 && (m == null || AiCatalog.Unsuitable(m) == null))
                {
                    var a = MessageBox.Show(this,
                        L.F("L'assistente locale va ancora scaricato ({0}). Scaricarlo adesso?", "The local assistant still has to be downloaded ({0}). Download it now?", Util.FormatBytes(need)),
                        Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (a == DialogResult.Cancel) return;
                    if (a == DialogResult.Yes) { Apply(); StartDownload(); return; }
                }
            }
            Apply();
            // L'assistente locale riparte alla prossima richiesta se è cambiato qualcosa che lo riguarda
            foreach (var k in new[] { "ai.enabled", "ai.provider", "ai.local.model", "ai.local.variant" })
                if (Settings.Get(k, (string)null) != snapshot[k]) { LocalAi.Instance.Stop(); break; }
            Settings.Save();
            DialogResult = DialogResult.OK;
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (downloading)
            {
                if (MessageBox.Show(this, L.T("Il download è in corso. Fermarlo? La prossima volta riprenderà da dove è arrivato.",
                                              "The download is in progress. Stop it? Next time it will resume where it left off."),
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                dl.Cancel();
                downloading = false;
            }
            if (testCts != null) testCts.Cancel();
            if (speedCts != null) speedCts.Cancel();
            if (DialogResult != DialogResult.OK)
            {
                Restore();
                if (testedLocal) LocalAi.Instance.Stop();   // era partito con le impostazioni della prova
            }
        }
    }
}
