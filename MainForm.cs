// Finestra principale: menu, schede, editor, barra di ricerca, barra di stato e sessione.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AppPad
{
    partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        readonly bool primary;
        readonly List<string> startFiles;
        readonly float scale;
        Theme theme = Theme.Light;
        string themeSetting;

        readonly MenuStrip menu = new MenuStrip();
        readonly TabStrip tabs = new TabStrip();
        readonly Panel host = new Panel();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel sPos = new ToolStripStatusLabel(), sSel = new ToolStripStatusLabel(), sSpring = new ToolStripStatusLabel(),
                                     sLines = new ToolStripStatusLabel(), sZoom = new ToolStripStatusLabel();
        readonly ToolStripDropDownButton sEol = new ToolStripDropDownButton(), sEnc = new ToolStripDropDownButton();
        readonly ContextMenuStrip editMenu = new ContextMenuStrip(), tabMenu = new ContextMenuStrip(), listMenu = new ContextMenuStrip();

        // Barra di ricerca
        readonly Panel findBar = new Panel();
        readonly FlowLayoutPanel findRow = new FlowLayoutPanel(), replaceRow = new FlowLayoutPanel();
        readonly TextBox findBox = new TextBox(), replaceBox = new TextBox();
        readonly CheckBox chkCase = new CheckBox(), chkWord = new CheckBox(), chkRegex = new CheckBox();
        readonly Label findMsg = new Label(), lblFind = new Label(), lblReplace = new Label();
        readonly Button btnPrev = new Button(), btnNext = new Button(), btnReplace = new Button(), btnReplaceAll = new Button(), btnCloseFind = new Button();

        // Caricamento in corso
        readonly Panel overlay = new Panel();
        readonly Label overlayText = new Label();
        readonly ProgressBar overlayBar = new ProgressBar();
        readonly Button overlayCancel = new Button();

        SciEditor loaderFactory;     // editor nascosto usato solo per creare i loader
        Doc active;
        ToolStripMenuItem miAutoUpdate;
        readonly Timer uiTimer = new Timer { Interval = 150 }, sessionTimer = new Timer { Interval = 30000 };
        bool sessionDirty, checkingFiles, applyingZoom, closing;
        readonly Stack<string> closedPaths = new Stack<string>();

        bool wrap, lineNumbers, showWs, showEol;
        string fontName;
        float fontSize;
        int zoom;

        ToolStripMenuItem miWrap, miLineNumbers, miShowWs, miShowEol, miStatus, miRecent, miEncoding, miLang, miTheme;

        List<Doc> Docs { get { return tabs.Docs; } }
        SciEditor Ed { get { return active != null && active.Loaded ? active.Editor : null; } }

        public MainForm(List<string> files, bool primary)
        {
            this.primary = primary;
            startFiles = files;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;

            Text = "AppPad";
            Icon = Util.AppIcon();
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(S(1000), S(700));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            AllowDrop = true;

            wrap = Settings.Get("wrap", false);
            lineNumbers = Settings.Get("lineNumbers", true);
            showWs = Settings.Get("showWs", false);
            showEol = Settings.Get("showEol", false);
            fontName = Settings.Get("fontName", "Consolas");
            fontSize = Math.Max(6f, Math.Min(72f, Settings.Get("fontSize", 11f)));
            zoom = Math.Max(-10, Math.Min(40, Settings.Get("zoom", 0)));
            themeSetting = Settings.Get("theme", "system");

            BuildUi();
            BuildMenu();
            RestoreBounds_();

            tabs.TabSelected += Activate;
            tabs.TabCloseRequested += d => CloseDoc(d);
            tabs.NewRequested += NewDoc;
            tabs.Reordered += delegate { sessionDirty = true; };
            tabs.TabContextMenu += ShowTabMenu;
            tabs.ListRequested += ShowTabList;

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) OpenFiles(f); };

            uiTimer.Tick += delegate { UpdateLoading(); };
            sessionTimer.Tick += delegate { if (sessionDirty && primary) SaveSession(false); };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Activated += delegate { if (!closing) BeginInvoke(new Action(CheckExternalChanges)); };
            // Aggiornamenti: un controllo al giorno dopo l'avvio; il menu mostra la versione nuova quando c'è
            Updater.Changed += delegate { if (!closing) BuildMenu(); };
            Shown += delegate { if (primary) Updater.CheckIfDue(this); };
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        // ======================================================================= UI

        void BuildUi()
        {
            host.Dock = DockStyle.Fill;
            tabs.Dock = DockStyle.Top;
            host.Resize += delegate { CenterOverlay(); };

            // Caricamento
            overlay.Size = new Size(S(380), S(120));
            overlay.Visible = false;
            overlayText.SetBounds(S(16), S(14), S(348), S(40));
            overlayText.AutoEllipsis = true;
            overlayBar.SetBounds(S(16), S(56), S(348), S(14));
            overlayBar.Maximum = 1000;
            overlayCancel.Text = L.T("Annulla", "Cancel");
            overlayCancel.SetBounds(S(264), S(80), S(100), S(28));
            overlayCancel.Click += delegate { if (active != null && active.Loading) CloseDoc(active); };
            overlay.Controls.AddRange(new Control[] { overlayText, overlayBar, overlayCancel });
            host.Controls.Add(overlay);

            // Ricerca
            findBar.Dock = DockStyle.Bottom;
            findBar.Visible = false;
            findBar.AutoSize = true;
            findBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            findBar.Padding = new Padding(S(6), S(4), S(6), S(4));
            foreach (var row in new[] { replaceRow, findRow })
            {
                row.Dock = DockStyle.Top; row.AutoSize = true; row.WrapContents = false;
                row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                findBar.Controls.Add(row);
            }
            lblFind.Text = L.T("Trova", "Find");
            lblReplace.Text = L.T("Sostituisci con", "Replace with");
            foreach (var l in new[] { lblFind, lblReplace })
            {
                l.AutoSize = false; l.Size = new Size(S(100), S(26)); l.TextAlign = ContentAlignment.MiddleLeft; l.Margin = new Padding(0, S(2), 0, 0);
            }
            findBox.Width = replaceBox.Width = S(300);
            findBox.Margin = replaceBox.Margin = new Padding(0, S(4), S(6), 0);
            SetupButton(btnPrev, "↑", L.T("Trova precedente (Maiusc+F3)", "Find previous (Shift+F3)"), S(32));
            SetupButton(btnNext, "↓", L.T("Trova successivo (F3)", "Find next (F3)"), S(32));
            SetupToggle(chkCase, "Aa", L.T("Maiuscole/minuscole", "Match case"), "findCase");
            SetupToggle(chkWord, "ab|", L.T("Parola intera", "Whole word"), "findWord");
            SetupToggle(chkRegex, ".*", L.T("Espressione regolare", "Regular expression"), "findRegex");
            findMsg.AutoSize = true; findMsg.Margin = new Padding(S(8), S(8), 0, 0);
            SetupButton(btnCloseFind, "✕", L.T("Chiudi (Esc)", "Close (Esc)"), S(32));
            SetupButton(btnReplace, L.T("Sostituisci", "Replace"), null, S(110));
            SetupButton(btnReplaceAll, L.T("Sostituisci tutto", "Replace all"), null, S(130));
            findRow.Controls.AddRange(new Control[] { lblFind, findBox, btnPrev, btnNext, chkCase, chkWord, chkRegex, btnCloseFind, findMsg });
            replaceRow.Controls.AddRange(new Control[] { lblReplace, replaceBox, btnReplace, btnReplaceAll });

            btnPrev.Click += delegate { FindNext(false); };
            btnNext.Click += delegate { FindNext(true); };
            btnReplace.Click += delegate { ReplaceOne(); };
            btnReplaceAll.Click += delegate { ReplaceAll(); };
            btnCloseFind.Click += delegate { CloseFind(); };
            findBox.TextChanged += delegate { IncrementalFind(); };
            findBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindNext(!e.Shift); }
            };
            replaceBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ReplaceOne(); }
            };

            // Barra di stato
            status.SizingGrip = false;
            status.ShowItemToolTips = true;
            sSpring.Spring = true;
            foreach (var it in new ToolStripItem[] { sPos, sSel, sAi, sSpring, sLines, sZoom, sEol, sEnc })
            {
                it.Padding = new Padding(S(6), 0, S(6), 0);
                status.Items.Add(it);
            }
            sEol.ShowDropDownArrow = sEnc.ShowDropDownArrow = false;
            sEol.ToolTipText = L.T("Fine riga", "Line endings");
            sEnc.ToolTipText = L.T("Codifica", "Encoding");
            sZoom.ToolTipText = "Zoom (Ctrl + rotellina)";
            sEol.DropDownOpening += delegate { sEol.DropDownItems.Clear(); AddEolItems(sEol.DropDownItems); };
            sEnc.DropDownOpening += delegate { sEnc.DropDownItems.Clear(); AddEncodingItems(sEnc.DropDownItems, true); };
            sEol.DropDownItems.Add("-"); sEnc.DropDownItems.Add("-"); // perché compaia la freccia di apertura al clic

            // Menu del tasto destro nell'editor
            editMenu.Opening += delegate { BuildEditMenu(); };
            InitAi();

            BuildAiPanel();

            // Ordine di aggancio: menu, barra di stato e schede intorno; il pannello dell'assistente a lato
            // (o sotto) con il suo divisore; la barra di ricerca solo sotto l'editor
            Controls.Add(host);
            Controls.Add(findBar);
            Controls.Add(aiSplit);
            Controls.Add(aiPanel);
            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        void SetupButton(Button b, string text, string tip, int width)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.Size = new Size(width, S(26));
            b.Margin = new Padding(0, S(3), S(4), 0);
            b.UseVisualStyleBackColor = false;
            if (tip != null) new ToolTip().SetToolTip(b, tip);
        }

        void SetupToggle(CheckBox c, string text, string tip, string key)
        {
            c.Text = text;
            c.Appearance = Appearance.Button;
            c.FlatStyle = FlatStyle.Flat;
            c.TextAlign = ContentAlignment.MiddleCenter;
            c.Size = new Size(S(36), S(26));
            c.Margin = new Padding(0, S(3), S(4), 0);
            c.Checked = Settings.Get(key, false);
            c.CheckedChanged += delegate { Settings.Set(key, c.Checked); IncrementalFind(); };
            new ToolTip().SetToolTip(c, tip);
        }

        ToolStripMenuItem Mi(string text, Keys keys, Action a, string display = null)
        {
            var mi = new ToolStripMenuItem(text);
            if (keys != Keys.None) mi.ShortcutKeys = keys;
            if (display != null) mi.ShortcutKeyDisplayString = display;
            if (a != null) mi.Click += delegate { a(); };
            return mi;
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.Padding = new Padding(S(4), S(2), 0, S(2));

            var file = new ToolStripMenuItem(L.T("&File", "&File"));
            miRecent = new ToolStripMenuItem(L.T("File &recenti", "&Recent files"));
            miRecent.DropDownItems.Add("-");
            miRecent.DropDownOpening += delegate { BuildRecent(); };
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(L.T("&Nuovo", "&New"), Keys.Control | Keys.N, NewDoc),
                Mi(L.T("&Apri...", "&Open..."), Keys.Control | Keys.O, OpenDialog),
                miRecent,
                Mi(L.T("Riapri scheda chiusa", "Reopen closed tab"), Keys.Control | Keys.Shift | Keys.T, ReopenClosed),
                Mi(L.T("Ricarica dal disco", "Reload from disk"), Keys.None, delegate { if (active != null) Reload(active, null); }),
                new ToolStripSeparator(),
                Mi(L.T("&Salva", "&Save"), Keys.Control | Keys.S, delegate { if (active != null) SaveDoc(active, false); }),
                Mi(L.T("Salva &con nome...", "Save &as..."), Keys.Control | Keys.Shift | Keys.S, delegate { if (active != null) SaveDoc(active, true); }),
                Mi(L.T("Salva &tutto", "Save a&ll"), Keys.Control | Keys.Alt | Keys.S, SaveAll),
                new ToolStripSeparator(),
                Mi(L.T("C&hiudi scheda", "&Close tab"), Keys.Control | Keys.W, delegate { if (active != null) CloseDoc(active); }),
                Mi(L.T("Chiudi tutte le schede", "Close all tabs"), Keys.Control | Keys.Shift | Keys.W, delegate { CloseMany(Docs.ToList()); }),
                new ToolStripSeparator(),
                Mi(L.T("Apri la cartella del file", "Open containing folder"), Keys.None, delegate { if (active != null && active.Path != null) Util.SelectInExplorer(active.Path); }),
                Mi(L.T("Copia percorso", "Copy path"), Keys.None, delegate { CopyPath(active); }),
                new ToolStripSeparator(),
                Mi(L.T("&Esci", "E&xit"), Keys.None, Close, "Alt+F4")
            });

            var edit = new ToolStripMenuItem(L.T("&Modifica", "&Edit"));
            edit.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(L.T("&Annulla", "&Undo"), Keys.None, delegate { EditCmd(Sci.SCI_UNDO); }, "Ctrl+Z"),
                Mi(L.T("&Ripeti", "&Redo"), Keys.None, delegate { EditCmd(Sci.SCI_REDO); }, "Ctrl+Y"),
                new ToolStripSeparator(),
                Mi(L.T("&Taglia", "Cu&t"), Keys.None, delegate { EditCmd(Sci.SCI_CUT); }, "Ctrl+X"),
                Mi(L.T("&Copia", "&Copy"), Keys.None, delegate { EditCmd(Sci.SCI_COPY); }, "Ctrl+C"),
                Mi(L.T("&Incolla", "&Paste"), Keys.None, delegate { EditCmd(Sci.SCI_PASTE); }, "Ctrl+V"),
                Mi(L.T("&Elimina", "&Delete"), Keys.None, delegate { EditCmd(Sci.SCI_CLEAR); }, L.T("Canc", "Del")),
                Mi(L.T("Seleziona t&utto", "Select &all"), Keys.None, delegate { EditCmd(Sci.SCI_SELECTALL); }, "Ctrl+A"),
                new ToolStripSeparator(),
                Mi(L.T("Duplica riga o selezione", "Duplicate line or selection"), Keys.None, delegate { EdCmd(Sci.SCI_SELECTIONDUPLICATE); }, "Ctrl+D"),
                Mi(L.T("Elimina riga", "Delete line"), Keys.None, delegate { EdCmd(Sci.SCI_LINEDELETE); }, "Ctrl+Maiusc+L"),
                Mi(L.T("MAIUSCOLE", "UPPERCASE"), Keys.None, delegate { EdCmd(Sci.SCI_UPPERCASE); }, "Ctrl+Maiusc+U"),
                Mi(L.T("minuscole", "lowercase"), Keys.None, delegate { EdCmd(Sci.SCI_LOWERCASE); }, "Ctrl+U"),
                new ToolStripSeparator(),
                Mi(L.T("Inserisci data e ora", "Insert time/date"), Keys.F5, InsertDateTime)
            });

            var search = new ToolStripMenuItem(L.T("&Cerca", "&Search"));
            search.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(L.T("&Trova...", "&Find..."), Keys.Control | Keys.F, delegate { ShowFind(false); }),
                Mi(L.T("Trova &successivo", "Find &next"), Keys.F3, delegate { FindNext(true); }),
                Mi(L.T("Trova &precedente", "Find &previous"), Keys.Shift | Keys.F3, delegate { FindNext(false); }),
                Mi(L.T("&Sostituisci...", "&Replace..."), Keys.Control | Keys.H, delegate { ShowFind(true); }),
                new ToolStripSeparator(),
                Mi(L.T("&Vai a riga...", "&Go to line..."), Keys.Control | Keys.G, GoToLine)
            });

            var view = new ToolStripMenuItem(L.T("&Visualizza", "&View"));
            miWrap = Mi(L.T("&A capo automatico", "&Word wrap"), Keys.Alt | Keys.Z, delegate { wrap = !wrap; Settings.Set("wrap", wrap); ApplyViewAll(); });
            miLineNumbers = Mi(L.T("&Numeri di riga", "&Line numbers"), Keys.None, delegate { lineNumbers = !lineNumbers; Settings.Set("lineNumbers", lineNumbers); ApplyViewAll(); });
            miShowWs = Mi(L.T("Mostra &spazi e tabulazioni", "Show &whitespace"), Keys.None, delegate { showWs = !showWs; Settings.Set("showWs", showWs); ApplyViewAll(); });
            miShowEol = Mi(L.T("Mostra &fine riga", "Show line &endings"), Keys.None, delegate { showEol = !showEol; Settings.Set("showEol", showEol); ApplyViewAll(); });
            miStatus = Mi(L.T("&Barra di stato", "&Status bar"), Keys.None, delegate { status.Visible = !status.Visible; Settings.Set("statusBar", status.Visible); miStatus.Checked = status.Visible; });
            status.Visible = Settings.Get("statusBar", true);
            var zoomMenu = new ToolStripMenuItem("&Zoom");
            zoomMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                Mi(L.T("Aumenta", "Zoom in"), Keys.Control | Keys.Oemplus, delegate { SetZoom(zoom + 1); }, "Ctrl++"),
                Mi(L.T("Riduci", "Zoom out"), Keys.Control | Keys.OemMinus, delegate { SetZoom(zoom - 1); }, "Ctrl+-"),
                Mi(L.T("Ripristina", "Reset"), Keys.Control | Keys.D0, delegate { SetZoom(0); }, "Ctrl+0")
            });
            miTheme = new ToolStripMenuItem(L.T("&Tema", "&Theme"));
            foreach (var t in new[] { new[] { "system", L.T("Come Windows", "Same as Windows") }, new[] { "light", L.T("Chiaro", "Light") }, new[] { "dark", L.T("Scuro", "Dark") } })
            {
                string key = t[0];
                var mi = Mi(t[1], Keys.None, delegate { themeSetting = key; Settings.Set("theme", key); ApplyTheme(); });
                mi.Tag = key;
                miTheme.DropDownItems.Add(mi);
            }
            view.DropDownItems.AddRange(new ToolStripItem[]
            {
                miWrap, miLineNumbers, miShowWs, miShowEol, new ToolStripSeparator(),
                zoomMenu, Mi(L.T("&Carattere...", "&Font..."), Keys.None, ChooseFont), miTheme, new ToolStripSeparator(), miStatus
            });
            view.DropDownOpening += delegate
            {
                miWrap.Checked = wrap; miLineNumbers.Checked = lineNumbers; miShowWs.Checked = showWs; miShowEol.Checked = showEol; miStatus.Checked = status.Visible;
                foreach (ToolStripMenuItem mi in miTheme.DropDownItems) mi.Checked = (string)mi.Tag == themeSetting;
            };

            miEncoding = new ToolStripMenuItem(L.T("C&odifica", "E&ncoding"));
            miEncoding.DropDownItems.Add("-");
            miEncoding.DropDownOpening += delegate
            {
                miEncoding.DropDownItems.Clear();
                AddEncodingItems(miEncoding.DropDownItems, true);
                miEncoding.DropDownItems.Add(new ToolStripSeparator());
                var eol = new ToolStripMenuItem(L.T("Fine riga", "Line endings"));
                AddEolItems(eol.DropDownItems);
                miEncoding.DropDownItems.Add(eol);
            };

            var help = new ToolStripMenuItem("&?");
            miLang = new ToolStripMenuItem("Lingua / Language");
            miLang.DropDownItems.Add("-");
            miLang.DropDownOpening += delegate
            {
                miLang.DropDownItems.Clear();
                var auto = Mi(L.T("Automatica (lingua di Windows)", "Automatic (Windows language)"), Keys.None, delegate { SetLanguage("auto"); });
                auto.Checked = L.Setting == "auto";
                miLang.DropDownItems.Add(auto);
                miLang.DropDownItems.Add(new ToolStripSeparator());
                foreach (var lang in L.All())
                {
                    string code = lang.Code;
                    var mi = Mi(lang.Name, Keys.None, delegate { SetLanguage(code); });
                    mi.Checked = L.Setting == code;
                    miLang.DropDownItems.Add(mi);
                }
            };
            help.DropDownItems.AddRange(new ToolStripItem[]
            {
                miLang, new ToolStripSeparator(),
                Mi(L.T("&Controlla aggiornamenti", "&Check for updates"), Keys.None, delegate { Updater.Check(this, true); }),
                miAutoUpdate = Mi(L.T("&Aggiorna automaticamente", "&Update automatically"), Keys.None, delegate
                {
                    Updater.Auto = !Updater.Auto;
                    miAutoUpdate.Checked = Updater.Auto;
                    if (Updater.Auto) Updater.CheckIfDue(this);
                }),
                new ToolStripSeparator(),
                Mi(L.T("Informazioni su AppPad", "About AppPad"), Keys.None, delegate { AboutForm.ShowFor(this, Icon); })
            });
            miAutoUpdate.Checked = Updater.Auto;
            miAutoUpdate.ToolTipText = L.T("Una volta al giorno controlla se c'è una versione nuova su GitHub, la scarica e la installa quando chiudi AppPad",
                                           "Once a day checks GitHub for a new version, downloads it and installs it when you close AppPad");

            menu.Items.AddRange(new ToolStripItem[] { file, edit, search, BuildAiMenu(false), view, miEncoding, help });
            // Sempre a portata di clic, a destra della barra dei menu: apre e chiude il pannello dell'assistente
            var ask = new ToolStripMenuItem(L.T("Chiedi all'assistente", "Ask the assistant"))
            {
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = L.T("Apre o chiude il pannello dell'assistente (F1)", "Opens or closes the assistant panel (F1)")
            };
            ask.Click += delegate { ToggleAiPanel(); };
            menu.Items.Add(ask);
            // Versione nuova disponibile (o già scaricata e verificata): un clic per installarla
            if (Updater.Version != null)
            {
                var upd = new ToolStripMenuItem(Updater.ReadyFile != null
                        ? L.F("Aggiornamento pronto: AppPad {0}", "Update ready: AppPad {0}", Updater.Version)
                        : L.F("AppPad {0} disponibile", "AppPad {0} available", Updater.Version))
                {
                    Alignment = ToolStripItemAlignment.Right,
                    ForeColor = theme != null ? theme.Accent : SystemColors.HotTrack,
                    ToolTipText = L.T("Installa adesso la versione nuova; altrimenti si installa da sola quando chiudi AppPad",
                                      "Install the new version now; otherwise it installs by itself when you close AppPad")
                };
                upd.Click += delegate { Updater.Offer(this); };
                menu.Items.Add(upd);
            }
        }

        void SetLanguage(string code)
        {
            L.Set(code);
            Settings.Set("lang", L.Setting);
            Settings.Save();
            BuildMenu();
            lblFind.Text = L.T("Trova", "Find");
            lblReplace.Text = L.T("Sostituisci con", "Replace with");
            btnReplace.Text = L.T("Sostituisci", "Replace");
            btnReplaceAll.Text = L.T("Sostituisci tutto", "Replace all");
            overlayCancel.Text = L.T("Annulla", "Cancel");
            ApplyTheme();
            tabs.Invalidate();
            UpdateTitle();
            UpdateStatus();
        }

        void AddEncodingItems(ToolStripItemCollection items, bool withReopen)
        {
            var d = active;
            var header = new ToolStripMenuItem(L.T("Salva con la codifica:", "Save with encoding:")) { Enabled = false };
            items.Add(header);
            foreach (Enc e in Enum.GetValues(typeof(Enc)))
            {
                Enc enc = e;
                var mi = Mi(TextFile.Name(enc), Keys.None, delegate { ConvertEncoding(enc); });
                mi.Checked = d != null && d.Encoding == enc;
                mi.Enabled = d != null && d.Loaded;
                items.Add(mi);
            }
            if (!withReopen) return;
            items.Add(new ToolStripSeparator());
            var reopen = new ToolStripMenuItem(L.T("Riapri con la codifica", "Reopen with encoding"));
            reopen.Enabled = d != null && d.Path != null && d.Loaded;
            foreach (Enc e in Enum.GetValues(typeof(Enc)))
            {
                Enc enc = e;
                reopen.DropDownItems.Add(Mi(TextFile.Name(enc), Keys.None, delegate { if (active != null) Reload(active, enc); }));
            }
            items.Add(reopen);
        }

        void AddEolItems(ToolStripItemCollection items)
        {
            var d = active;
            foreach (int eol in new[] { Sci.SC_EOL_CRLF, Sci.SC_EOL_LF, Sci.SC_EOL_CR })
            {
                int v = eol;
                var mi = Mi(TextFile.EolName(v), Keys.None, delegate { SetEol(v); });
                mi.Checked = d != null && d.Eol == v;
                mi.Enabled = d != null && d.Loaded;
                items.Add(mi);
            }
        }

        void BuildRecent()
        {
            miRecent.DropDownItems.Clear();
            var list = Recent();
            foreach (var p in list)
            {
                string path = p;
                miRecent.DropDownItems.Add(Mi(path.Replace("&", "&&"), Keys.None, delegate { OpenFiles(new[] { path }); }));
            }
            if (list.Count == 0) miRecent.DropDownItems.Add(new ToolStripMenuItem(L.T("(nessuno)", "(none)")) { Enabled = false });
            else
            {
                miRecent.DropDownItems.Add(new ToolStripSeparator());
                miRecent.DropDownItems.Add(Mi(L.T("Svuota elenco", "Clear list"), Keys.None, delegate { Settings.Set("recent", ""); Settings.Save(); }));
            }
        }

        static List<string> Recent()
        {
            return Settings.Get("recent", "").Split('|').Where(s => s.Length > 0).ToList();
        }

        static void AddRecent(string path)
        {
            var list = Recent();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            Settings.Set("recent", string.Join("|", list.Take(15)));
        }

        void BuildEditMenu()
        {
            editMenu.Items.Clear();
            var ed = Ed;
            bool sel = ed != null && ed.Send(Sci.SCI_GETSELECTIONEMPTY) == 0;
            var undo = Mi(L.T("Annulla", "Undo"), Keys.None, delegate { EdCmd(Sci.SCI_UNDO); }, "Ctrl+Z");
            undo.Enabled = ed != null && ed.Send(Sci.SCI_CANUNDO) != 0;
            var redo = Mi(L.T("Ripeti", "Redo"), Keys.None, delegate { EdCmd(Sci.SCI_REDO); }, "Ctrl+Y");
            redo.Enabled = ed != null && ed.Send(Sci.SCI_CANREDO) != 0;
            var cut = Mi(L.T("Taglia", "Cut"), Keys.None, delegate { EdCmd(Sci.SCI_CUT); }, "Ctrl+X");
            var copy = Mi(L.T("Copia", "Copy"), Keys.None, delegate { EdCmd(Sci.SCI_COPY); }, "Ctrl+C");
            var del = Mi(L.T("Elimina", "Delete"), Keys.None, delegate { EdCmd(Sci.SCI_CLEAR); }, L.T("Canc", "Del"));
            cut.Enabled = copy.Enabled = del.Enabled = sel;
            editMenu.Items.AddRange(new ToolStripItem[]
            {
                undo, redo, new ToolStripSeparator(), cut, copy,
                Mi(L.T("Incolla", "Paste"), Keys.None, delegate { EdCmd(Sci.SCI_PASTE); }, "Ctrl+V"),
                del, new ToolStripSeparator(),
                Mi(L.T("Seleziona tutto", "Select all"), Keys.None, delegate { EdCmd(Sci.SCI_SELECTALL); }, "Ctrl+A")
            });
            editMenu.Items.Add(new ToolStripSeparator());
            editMenu.Items.Add(BuildAiMenu(true));
        }

        void ShowTabMenu(Doc d, Point pt)
        {
            tabMenu.Items.Clear();
            int i = Docs.IndexOf(d);
            var closeRight = Mi(L.T("Chiudi le schede a destra", "Close tabs to the right"), Keys.None, delegate { CloseMany(Docs.Skip(Docs.IndexOf(d) + 1).ToList()); });
            closeRight.Enabled = i < Docs.Count - 1;
            var others = Mi(L.T("Chiudi le altre schede", "Close other tabs"), Keys.None, delegate { CloseMany(Docs.Where(x => x != d).ToList()); });
            others.Enabled = Docs.Count > 1;
            var folder = Mi(L.T("Apri la cartella del file", "Open containing folder"), Keys.None, delegate { Util.SelectInExplorer(d.Path); });
            var copy = Mi(L.T("Copia percorso", "Copy path"), Keys.None, delegate { CopyPath(d); });
            folder.Enabled = copy.Enabled = d.Path != null;
            tabMenu.Items.AddRange(new ToolStripItem[]
            {
                Mi(L.T("Chiudi", "Close"), Keys.None, delegate { CloseDoc(d); }, "Ctrl+W"),
                others, closeRight, new ToolStripSeparator(), copy, folder
            });
            tabMenu.Show(tabs, pt);
        }

        void ShowTabList()
        {
            listMenu.Items.Clear();
            foreach (var d in Docs)
            {
                var doc = d;
                var mi = Mi((d.Dirty ? "● " : "") + d.Title.Replace("&", "&&"), Keys.None, delegate { Activate(doc); });
                mi.Checked = d == active;
                listMenu.Items.Add(mi);
            }
            var p = tabs.ListMenuLocation;
            listMenu.Show(tabs, p, ToolStripDropDownDirection.BelowLeft);
        }

        static void CopyPath(Doc d)
        {
            if (d != null && d.Path != null) try { Clipboard.SetText(d.Path); } catch { }
        }

        // ======================================================================= Tema

        void ApplyTheme()
        {
            theme = Theme.FromSetting(themeSetting);
            var t = theme;
            ToolStripManager.Renderer = new ThemedRenderer(t);
            menu.Renderer = status.Renderer = editMenu.Renderer = tabMenu.Renderer = listMenu.Renderer = new ThemedRenderer(t);
            BackColor = t.Chrome;
            menu.BackColor = status.BackColor = t.Chrome;
            menu.ForeColor = status.ForeColor = t.ChromeText;
            tabs.Theme = t;
            tabs.Invalidate();
            host.BackColor = t.EditorBack;

            findBar.BackColor = t.Chrome;
            foreach (Control c in findRow.Controls.Cast<Control>().Concat(replaceRow.Controls.Cast<Control>()))
            {
                c.ForeColor = t.ChromeText;
                if (c is TextBox) { c.BackColor = t.InputBack; c.ForeColor = t.InputFore; ((TextBox)c).BorderStyle = BorderStyle.FixedSingle; }
                else if (c is Button) StyleFlat((ButtonBase)c, t);
                else if (c is CheckBox) { StyleFlat((ButtonBase)c, t); ((CheckBox)c).FlatAppearance.CheckedBackColor = t.Pressed; }
            }
            overlay.BackColor = t.Chrome;
            overlayText.ForeColor = t.ChromeText;
            StyleFlat(overlayCancel, t);
            ApplyAiPanelTheme(t);

            foreach (var d in Docs) if (d.Editor != null) { ApplyStyle(d.Editor); d.MarginDigits = 0; UpdateMargin(d); }
            try
            {
                int dark = t.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, 4);
            }
            catch { }
            UpdateStatus();
        }

        static void StyleFlat(ButtonBase b, Theme t)
        {
            b.BackColor = t.Chrome;
            b.ForeColor = t.ChromeText;
            b.FlatAppearance.BorderColor = t.Border;
            b.FlatAppearance.MouseOverBackColor = t.Hover;
            b.FlatAppearance.MouseDownBackColor = t.Pressed;
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (themeSetting == "system" && e.Category == UserPreferenceCategory.General)
                try { BeginInvoke(new Action(ApplyTheme)); } catch { }
        }

        // ======================================================================= Editor

        SciEditor EnsureEditor(Doc d)
        {
            if (d.Editor != null) return d.Editor;
            var ed = new SciEditor { Dock = DockStyle.Fill, Visible = false, Tag = d };
            host.Controls.Add(ed);
            var h = ed.Handle; // crea subito la finestra nativa
            ed.Notify += OnSciNotify;
            ed.FilesDropped += files => BeginInvoke(new Action(delegate { OpenFiles(files); }));
            ed.ContextMenuStrip = editMenu;

            ed.Send(Sci.SCI_SETTECHNOLOGY, Sci.SC_TECHNOLOGY_DIRECTWRITE);
            ed.Send(Sci.SCI_SETFONTQUALITY, Sci.SC_EFF_QUALITY_LCD_OPTIMIZED);
            ed.Send(Sci.SCI_SETLAYOUTCACHE, Sci.SC_CACHE_PAGE);
            ed.Send(Sci.SCI_SETLAYOUTTHREADS, Math.Max(1, Environment.ProcessorCount));
            ed.Send(Sci.SCI_USEPOPUP, 0);
            ed.Send(Sci.SCI_SETMODEVENTMASK, Sci.SC_MOD_INSERTTEXT | Sci.SC_MOD_DELETETEXT);
            ed.Send(Sci.SCI_SETMULTIPLESELECTION, 1);
            ed.Send(Sci.SCI_SETADDITIONALSELECTIONTYPING, 1);
            ed.Send(Sci.SCI_SETMULTIPASTE, Sci.SC_MULTIPASTE_EACH);
            ed.Send(Sci.SCI_SETMOUSESELECTIONRECTANGULARSWITCH, 1);
            ed.Send(Sci.SCI_SETVIRTUALSPACEOPTIONS, Sci.SCVS_RECTANGULARSELECTION);
            ed.Send(Sci.SCI_SETSCROLLWIDTH, 1);
            ed.Send(Sci.SCI_SETSCROLLWIDTHTRACKING, 1);
            ed.Send(Sci.SCI_SETCHARACTERCATEGORYOPTIMIZATION, 0x10000);
            ed.Send(Sci.SCI_SETMARGINS, 1);
            ed.Send(Sci.SCI_SETMARGINTYPEN, 0, Sci.SC_MARGIN_NUMBER);
            ed.Send(Sci.SCI_SETMARGINLEFT, 0, S(4));
            ed.Send(Sci.SCI_SETYCARETPOLICY, Sci.CARET_SLOP | Sci.CARET_STRICT | Sci.CARET_EVEN, 3);
            ed.NewEmptyDocument();
            ApplyDocSettings(d, ed);
            ApplyStyle(ed);
            ApplyView(d, ed);
            d.Editor = ed;
            return ed;
        }

        void ApplyDocSettings(Doc d, SciEditor ed)
        {
            ed.Send(Sci.SCI_SETCODEPAGE, Sci.SC_CP_UTF8);
            ed.Send(Sci.SCI_SETEOLMODE, d.Eol);
            ed.Send(Sci.SCI_SETTABWIDTH, 4);
            ed.Send(Sci.SCI_SETUSETABS, 1);
            ed.Send(Sci.SCI_SETPASTECONVERTENDINGS, 1);
        }

        void ApplyStyle(SciEditor ed)
        {
            var t = theme;
            ed.SendStr(Sci.SCI_STYLESETFONT, Sci.STYLE_DEFAULT, fontName);
            ed.Send(Sci.SCI_STYLESETSIZEFRACTIONAL, Sci.STYLE_DEFAULT, (long)Math.Round(fontSize * 100));
            ed.Send(Sci.SCI_STYLESETFORE, Sci.STYLE_DEFAULT, SciEditor.Rgb(t.EditorFore));
            ed.Send(Sci.SCI_STYLESETBACK, Sci.STYLE_DEFAULT, SciEditor.Rgb(t.EditorBack));
            ed.Send(Sci.SCI_STYLECLEARALL);
            ed.Send(Sci.SCI_STYLESETFORE, Sci.STYLE_LINENUMBER, SciEditor.Rgb(t.LineNumFore));
            ed.Send(Sci.SCI_STYLESETBACK, Sci.STYLE_LINENUMBER, SciEditor.Rgb(t.LineNumBack));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_SELECTION_BACK, SciEditor.Rgba(t.Selection));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_SELECTION_ADDITIONAL_BACK, SciEditor.Rgba(t.Selection));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_SELECTION_INACTIVE_BACK, SciEditor.Rgba(Color.FromArgb(160, t.Selection)));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_SELECTION_INACTIVE_ADDITIONAL_BACK, SciEditor.Rgba(Color.FromArgb(160, t.Selection)));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_CARET, SciEditor.Rgba(t.Caret));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_CARET_ADDITIONAL, SciEditor.Rgba(t.Caret));
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_CARET_LINE_BACK, SciEditor.Rgba(t.CaretLine));
            ed.Send(Sci.SCI_SETCARETLINEVISIBLEALWAYS, 1);
            ed.Send(Sci.SCI_SETELEMENTCOLOUR, Sci.SC_ELEMENT_WHITE_SPACE, SciEditor.Rgba(t.Whitespace));
            ed.Send(Sci.SCI_SETCARETWIDTH, Math.Max(1, S(1.5f)));
            ed.SetDarkScrollbars(t.Dark);
        }

        void ApplyView(Doc d, SciEditor ed)
        {
            ed.Send(Sci.SCI_SETWRAPMODE, wrap ? Sci.SC_WRAP_WORD : Sci.SC_WRAP_NONE);
            ed.Send(Sci.SCI_SETVIEWWS, showWs ? 1 : 0);
            ed.Send(Sci.SCI_SETVIEWEOL, showEol ? 1 : 0);
            applyingZoom = true;
            ed.Send(Sci.SCI_SETZOOM, zoom);
            applyingZoom = false;
            d.MarginDigits = 0;
            UpdateMargin(d);
        }

        void ApplyViewAll()
        {
            foreach (var d in Docs) if (d.Editor != null) ApplyView(d, d.Editor);
            UpdateStatus();
        }

        // Larghezza del margine dei numeri di riga: cambia solo quando cambia il numero di cifre
        void UpdateMargin(Doc d)
        {
            var ed = d.Editor;
            if (ed == null) return;
            if (!lineNumbers) { ed.Send(Sci.SCI_SETMARGINWIDTHN, 0, 0); d.MarginDigits = -1; return; }
            int digits = Math.Max(3, ed.LineCount.ToString().Length);
            if (digits == d.MarginDigits) return;
            d.MarginDigits = digits;
            long w = ed.SendStr(Sci.SCI_TEXTWIDTH, Sci.STYLE_LINENUMBER, "_" + new string('9', digits));
            ed.Send(Sci.SCI_SETMARGINWIDTHN, 0, w + S(10));
        }

        void SetZoom(int z)
        {
            zoom = Math.Max(-10, Math.Min(40, z));
            Settings.Set("zoom", zoom);
            ApplyViewAll();
        }

        void OnSciNotify(SciEditor ed, SCNotification n)
        {
            var d = ed.Tag as Doc;
            if (d == null || d.Editor != ed) return;
            switch (n.code)
            {
                case Sci.SCN_SAVEPOINTREACHED:
                case Sci.SCN_SAVEPOINTLEFT:
                    tabs.Invalidate();
                    if (d == active) UpdateTitle();
                    sessionDirty = true;
                    break;
                case Sci.SCN_MODIFIED:
                    if ((n.modificationType & (Sci.SC_MOD_INSERTTEXT | Sci.SC_MOD_DELETETEXT)) != 0)
                    {
                        d.ChangedSinceBackup = true;
                        sessionDirty = true;
                        if (n.linesAdded != IntPtr.Zero) UpdateMargin(d);
                    }
                    break;
                case Sci.SCN_UPDATEUI:
                    if (d == active) UpdateStatus();
                    break;
                case Sci.SCN_ZOOM:
                    if (applyingZoom) break;
                    zoom = (int)ed.Send(Sci.SCI_GETZOOM);
                    Settings.Set("zoom", zoom);
                    ApplyViewAll();
                    break;
            }
        }

        void EdCmd(int msg)
        {
            var ed = Ed;
            if (ed != null) { ed.Send(msg); ed.Focus(); }
        }

        // Comandi del menu Modifica: valgono anche per le caselle della barra di ricerca
        void EditCmd(int msg)
        {
            var tb = ActiveControl as TextBox;
            if (tb == null && findBar.ContainsFocus) tb = findBox.Focused ? findBox : replaceBox.Focused ? replaceBox : null;
            if (tb != null)
            {
                switch (msg)
                {
                    case Sci.SCI_UNDO: tb.Undo(); break;
                    case Sci.SCI_CUT: tb.Cut(); break;
                    case Sci.SCI_COPY: tb.Copy(); break;
                    case Sci.SCI_PASTE: tb.Paste(); break;
                    case Sci.SCI_CLEAR: tb.SelectedText = ""; break;
                    case Sci.SCI_SELECTALL: tb.SelectAll(); break;
                }
                return;
            }
            EdCmd(msg);
        }

        void InsertDateTime()
        {
            var ed = Ed;
            if (ed == null) return;
            var now = DateTime.Now;
            ed.SendStr(Sci.SCI_REPLACESEL, 0, now.ToShortTimeString() + " " + now.ToShortDateString());
        }

        void ChooseFont()
        {
            using (var dlg = new FontDialog { ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false })
            {
                try { dlg.Font = new Font(fontName, fontSize); } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                fontName = dlg.Font.Name;
                fontSize = dlg.Font.SizeInPoints;
                Settings.Set("fontName", fontName);
                Settings.Set("fontSize", fontSize);
                foreach (var d in Docs) if (d.Editor != null) { ApplyStyle(d.Editor); d.MarginDigits = 0; UpdateMargin(d); }
                UpdateStatus();
            }
        }

        void GoToLine()
        {
            var ed = Ed;
            if (ed == null) return;
            long cur = ed.LineFromPosition(ed.CurrentPos) + 1, max = ed.LineCount;
            string s = InputForm.Ask(this, L.T("Vai a riga", "Go to line"), L.F("Numero di riga (1 - {0}):", "Line number (1 - {0}):", Util.N(max)), cur.ToString());
            long n;
            if (s == null || !long.TryParse(s.Replace(".", "").Replace(",", "").Trim(), out n)) { ed.Focus(); return; }
            n = Math.Max(1, Math.Min(max, n));
            ed.Send(Sci.SCI_ENSUREVISIBLEENFORCEPOLICY, n - 1);
            ed.Send(Sci.SCI_GOTOLINE, n - 1);
            ed.Focus();
        }

        void SetEol(int eol)
        {
            var d = active;
            if (d == null || !d.Loaded) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                d.Eol = eol;
                d.Editor.Send(Sci.SCI_SETEOLMODE, eol);
                d.Editor.Send(Sci.SCI_CONVERTEOLS, eol);
            }
            finally { Cursor = Cursors.Default; }
            UpdateStatus();
        }

        void ConvertEncoding(Enc enc)
        {
            var d = active;
            if (d == null || !d.Loaded || d.Encoding == enc) return;
            d.Encoding = enc;
            d.ExtraDirty = true;
            d.ChangedSinceBackup = true;
            sessionDirty = true;
            tabs.Invalidate();
            UpdateTitle();
            UpdateStatus();
        }

        // ======================================================================= Schede

        void Activate(Doc d)
        {
            if (d == null || d.Closed) return;
            var prev = active;
            active = d;
            tabs.Active = d;
            tabs.EnsureVisible(d);

            if (d.Editor == null)
            {
                EnsureEditor(d);
                if (d.BackupId != null) StartLoad(d, d.BackupFile, Enc.Utf8, true);
                else if (d.Path != null) StartLoad(d, d.Path, null, false);
            }
            if (d.Loading) { ShowOverlay(); }
            else
            {
                overlay.Visible = false;
                d.Editor.Visible = true;
                if (!findBar.ContainsFocus) d.Editor.Focus();
            }
            if (prev != null && prev != d && prev.Editor != null) prev.Editor.Visible = false;
            UpdateTitle();
            UpdateStatus();
            sessionDirty = true;
        }

        int NextUntitled()
        {
            int n = 1;
            while (Docs.Any(x => x.Path == null && x.UntitledNo == n)) n++;
            return n;
        }

        void NewDoc()
        {
            var d = new Doc { UntitledNo = NextUntitled() };
            int i = active != null ? Docs.IndexOf(active) + 1 : Docs.Count;
            Docs.Insert(Math.Max(0, Math.Min(i, Docs.Count)), d);
            EnsureEditor(d);
            Activate(d);
        }

        void OpenDialog()
        {
            using (var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = L.T("Tutti i file (*.*)|*.*|File di testo (*.txt)|*.txt", "All files (*.*)|*.*|Text files (*.txt)|*.txt"),
                Title = L.T("Apri", "Open")
            })
            {
                if (active != null && active.Path != null) try { dlg.InitialDirectory = Path.GetDirectoryName(active.Path); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenFiles(dlg.FileNames);
            }
        }

        // Scheda vuota senza titolo che si può sostituire con il primo file aperto
        bool IsBlank(Doc d)
        {
            return d != null && d.Path == null && d.Loaded && !d.Dirty && d.Editor.Length == 0;
        }

        void OpenFiles(IEnumerable<string> paths)
        {
            Doc last = null;
            Doc blank = IsBlank(active) ? active : null;
            foreach (var p0 in paths)
            {
                string p;
                try { p = Path.GetFullPath(p0); } catch { continue; }
                if (Directory.Exists(p)) continue;
                var existing = Docs.FirstOrDefault(x => x.Path != null && string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { last = existing; continue; }
                if (!File.Exists(p))
                {
                    MessageBox.Show(this, L.F("File non trovato:\n{0}", "File not found:\n{0}", p), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                var d = new Doc { Path = p };
                Docs.Add(d);
                EnsureEditor(d);
                if (!StartLoad(d, p, null, false)) { RemoveDoc(d, false); continue; }
                AddRecent(p);
                last = d;
            }
            if (last != null)
            {
                Activate(last);
                if (blank != null && blank != last && Docs.Count > 1 && IsBlank(blank)) RemoveDoc(blank, false);
            }
            sessionDirty = true;
        }

        void ReopenClosed()
        {
            while (closedPaths.Count > 0)
            {
                string p = closedPaths.Pop();
                if (File.Exists(p)) { OpenFiles(new[] { p }); return; }
            }
        }

        // Chiede di salvare se serve. false = l'utente ha annullato (o la chiusura avverrà dopo il caricamento).
        bool CloseDoc(Doc d)
        {
            if (d == null || d.Closed) return true;
            AbortAi(d);
            if (d.Loading)
            {
                if (d.ExtraDirty) { d.CloseAfterLoad = true; Activate(d); return false; }
                d.Job.Cancel = true;
                d.Job = null;
                RemoveDoc(d, true);
                return true;
            }
            if (d.Editor == null && d.ExtraDirty) { d.CloseAfterLoad = true; Activate(d); return false; }
            if (d.Dirty)
            {
                Activate(d);
                var r = MessageBox.Show(this, L.F("Salvare le modifiche a {0}?", "Save changes to {0}?", d.Path ?? d.Title), "AppPad",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.Yes && !SaveDoc(d, false)) return false;
            }
            RemoveDoc(d, true);
            return true;
        }

        void CloseMany(List<Doc> list)
        {
            foreach (var d in list) if (!CloseDoc(d)) break;
        }

        void RemoveDoc(Doc d, bool remember)
        {
            int i = Docs.IndexOf(d);
            if (i < 0) return;
            if (remember && d.Path != null) closedPaths.Push(d.Path);
            Docs.RemoveAt(i);
            d.Closed = true;
            if (d.Job != null) { d.Job.Cancel = true; d.Job = null; }
            if (d.Editor != null)
            {
                host.Controls.Remove(d.Editor);
                d.Editor.Dispose();
                d.Editor = null;
            }
            d.BackupId = null;
            sessionDirty = true;
            if (d == active)
            {
                active = null;
                if (Docs.Count == 0) NewDoc();
                else Activate(Docs[Math.Min(i, Docs.Count - 1)]);
            }
            tabs.Invalidate();
        }

        // ======================================================================= Apertura e salvataggio

        bool StartLoad(Doc d, string source, Enc? forced, bool fromBackup)
        {
            long size;
            try { size = new FileInfo(source).Length; }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("Impossibile aprire {0}:\n{1}", "Could not open {0}:\n{1}", source, ex.Message), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (!fromBackup && size > 1024L * 1024 * 1024)
            {
                if (MessageBox.Show(this, L.F("{0} è molto grande ({1}): aprirlo richiede molta memoria e un po' di tempo. Continuare?",
                        "{0} is very large ({1}): opening it needs a lot of memory and some time. Continue?", Path.GetFileName(source), Util.FormatBytes(size)),
                        "AppPad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            }
            var loader = loaderFactory.CreateLoader(size, size > 1536L * 1024 * 1024);
            if (loader == null)
            {
                MessageBox.Show(this, L.T("Memoria insufficiente per aprire il file.", "Not enough memory to open the file."), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (d.Editor != null && d.Loaded && d.RestoreCaret < 0)
            {
                d.RestoreCaret = d.Editor.CurrentPos;
                d.RestoreAnchor = d.Editor.AnchorPos;
                d.RestoreFirstLine = d.Editor.Send(Sci.SCI_GETFIRSTVISIBLELINE);
            }
            var job = new LoadJob { Source = source, Forced = forced, Total = size };
            d.Job = job;
            if (d == active) { d.Editor.Visible = false; ShowOverlay(); }
            uiTimer.Start();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                TextFile.Load(job, loader);
                try { BeginInvoke(new Action(delegate { FinishLoad(d, job, fromBackup); })); } catch { }
            });
            tabs.Invalidate();
            UpdateStatus();
            return true;
        }

        void FinishLoad(Doc d, LoadJob job, bool fromBackup)
        {
            if (d.Job != job || d.Closed)
            {
                // Annullato, chiuso o sostituito da un altro caricamento
                if (job.Document != IntPtr.Zero) loaderFactory.SendPtr(Sci.SCI_RELEASEDOCUMENT, 0, job.Document);
                return;
            }
            d.Job = null;
            var ed = d.Editor;
            if (job.Error != null || job.Cancelled)
            {
                if (job.Error != null)
                {
                    string msg = job.Error is OutOfMemoryException ? L.T("memoria insufficiente", "not enough memory") : job.Error.Message;
                    MessageBox.Show(this, L.F("Impossibile aprire {0}:\n{1}", "Could not open {0}:\n{1}", d.Path ?? d.Title, msg), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                if (ed.Length == 0 && !ed.Modified && d.RestoreCaret < 0) { RemoveDoc(d, false); return; }
            }
            else
            {
                ed.AttachDocument(job.Document);
                d.Eol = job.Eol;
                if (!fromBackup)
                {
                    d.Encoding = job.Encoding;
                    d.ExtraDirty = false;
                    try { var fi = new FileInfo(job.Source); d.FileTimeUtc = fi.LastWriteTimeUtc; d.FileSize = fi.Length; } catch { }
                }
                ApplyDocSettings(d, ed);
                ed.Send(Sci.SCI_SETUNDOCOLLECTION, 1);
                ed.Send(Sci.SCI_EMPTYUNDOBUFFER);
                ed.Send(Sci.SCI_SETSAVEPOINT);
                ApplyView(d, ed);
                long len = ed.Length;
                if (d.RestoreCaret >= 0 && d.RestoreCaret <= len)
                {
                    long anchor = d.RestoreAnchor >= 0 && d.RestoreAnchor <= len ? d.RestoreAnchor : d.RestoreCaret;
                    ed.Send(Sci.SCI_SETSEL, anchor, d.RestoreCaret);
                    if (d.RestoreFirstLine >= 0) ed.Send(Sci.SCI_SETFIRSTVISIBLELINE, d.RestoreFirstLine);
                }
                else ed.Send(Sci.SCI_GOTOPOS, 0);
                d.RestoreCaret = d.RestoreAnchor = d.RestoreFirstLine = -1;
                d.ChangedSinceBackup = false;
            }
            if (d == active)
            {
                overlay.Visible = false;
                ed.Visible = true;
                if (!findBar.ContainsFocus) ed.Focus();
            }
            tabs.Invalidate();
            UpdateTitle();
            UpdateStatus();
            sessionDirty = true;
            if (d.CloseAfterLoad) { d.CloseAfterLoad = false; BeginInvoke(new Action(delegate { CloseDoc(d); })); }
        }

        void ShowOverlay()
        {
            CenterOverlay();
            overlay.Visible = true;
            overlay.BringToFront();
            UpdateLoading();
        }

        void CenterOverlay()
        {
            overlay.Location = new Point(Math.Max(0, (host.ClientSize.Width - overlay.Width) / 2), Math.Max(0, (host.ClientSize.Height - overlay.Height) / 3));
        }

        void UpdateLoading()
        {
            bool any = Docs.Any(x => x.Loading);
            if (!any) uiTimer.Stop();
            tabs.Invalidate();
            var d = active;
            if (d == null || !d.Loading) return;
            var j = d.Job;
            int v = j.Total > 0 ? (int)Math.Min(1000, j.Done * 1000 / j.Total) : 0;
            overlayBar.Value = v;
            overlayText.Text = L.F("Caricamento di {0}...", "Loading {0}...", d.Title) + "\n" +
                               Util.FormatBytes(j.Done) + " / " + Util.FormatBytes(j.Total) + "  (" + (v / 10) + "%)";
        }

        bool SaveDoc(Doc d, bool saveAs)
        {
            if (d == null || !d.Loaded) return false;
            string path = d.Path;
            if (saveAs || path == null)
            {
                using (var dlg = new SaveFileDialog
                {
                    Filter = L.T("File di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*", "Text files (*.txt)|*.txt|All files (*.*)|*.*"),
                    Title = L.T("Salva con nome", "Save as"),
                    OverwritePrompt = true
                })
                {
                    if (d.Path != null)
                    {
                        dlg.FileName = Path.GetFileName(d.Path);
                        try { dlg.InitialDirectory = Path.GetDirectoryName(d.Path); } catch { }
                        dlg.FilterIndex = string.Equals(Path.GetExtension(d.Path), ".txt", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    }
                    else dlg.FileName = d.Title + ".txt";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    path = dlg.FileName;
                }
            }
            Enc enc = d.Encoding;
            Cursor = Cursors.WaitCursor;
            try
            {
                try { TextFile.Save(d.Editor, path, enc, false); }
                catch (System.Text.EncoderFallbackException)
                {
                    Cursor = Cursors.Default;
                    var r = MessageBox.Show(this, L.F("Alcuni caratteri non esistono nella codifica {0} e verrebbero sostituiti da \"?\".\n\nSalvare in UTF-8? (No = salva comunque in {0})",
                        "Some characters do not exist in the {0} encoding and would be replaced by \"?\".\n\nSave as UTF-8 instead? (No = save in {0} anyway)", TextFile.Name(enc)),
                        "AppPad", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (r == DialogResult.Cancel) return false;
                    Cursor = Cursors.WaitCursor;
                    if (r == DialogResult.Yes) { enc = Enc.Utf8; TextFile.Save(d.Editor, path, enc, false); }
                    else TextFile.Save(d.Editor, path, enc, true);
                }
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, L.F("Impossibile salvare {0}:\n{1}", "Could not save {0}:\n{1}", path, ex.Message), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally { Cursor = Cursors.Default; }

            d.Path = path;
            d.UntitledNo = 0;
            d.Encoding = enc;
            d.ExtraDirty = false;
            d.ChangedSinceBackup = false;
            d.BackupId = null;
            d.Editor.Send(Sci.SCI_SETSAVEPOINT);
            try { var fi = new FileInfo(path); d.FileTimeUtc = fi.LastWriteTimeUtc; d.FileSize = fi.Length; } catch { }
            AddRecent(path);
            sessionDirty = true;
            tabs.Invalidate();
            UpdateTitle();
            UpdateStatus();
            return true;
        }

        void SaveAll()
        {
            foreach (var d in Docs.ToList())
                if (d.Loaded && d.Dirty && !SaveDoc(d, false)) return;
        }

        void Reload(Doc d, Enc? enc)
        {
            if (d == null || d.Path == null || d.Loading || d.Editor == null) return;
            if (d.Dirty && MessageBox.Show(this, L.F("Ricaricare {0} dal disco? Le modifiche non salvate andranno perse.",
                    "Reload {0} from disk? Unsaved changes will be lost.", d.Title), "AppPad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (!File.Exists(d.Path))
            {
                MessageBox.Show(this, L.F("File non trovato:\n{0}", "File not found:\n{0}", d.Path), "AppPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StartLoad(d, d.Path, enc, false);
        }

        // File cambiati da un altro programma mentre erano aperti
        void CheckExternalChanges()
        {
            if (checkingFiles || closing) return;
            checkingFiles = true;
            try
            {
                foreach (var d in Docs.ToList())
                {
                    if (d.Closed || !d.Loaded || d.Path == null) continue;
                    var fi = new FileInfo(d.Path);
                    if (!fi.Exists) continue;
                    if (fi.LastWriteTimeUtc == d.FileTimeUtc && fi.Length == d.FileSize) continue;
                    d.FileTimeUtc = fi.LastWriteTimeUtc;
                    d.FileSize = fi.Length;
                    Activate(d);
                    string q = d.Dirty
                        ? L.F("{0}\n\nIl file è stato modificato da un altro programma. Ricaricarlo e perdere le modifiche fatte in AppPad?",
                              "{0}\n\nThe file was changed by another program. Reload it and lose the changes made in AppPad?", d.Path)
                        : L.F("{0}\n\nIl file è stato modificato da un altro programma. Ricaricarlo?",
                              "{0}\n\nThe file was changed by another program. Reload it?", d.Path);
                    if (MessageBox.Show(this, q, "AppPad", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        StartLoad(d, d.Path, null, false);
                }
            }
            catch { }
            finally { checkingFiles = false; }
        }

        // ======================================================================= Ricerca

        void ShowFind(bool replace)
        {
            var ed = Ed;
            if (ed != null)
            {
                long s = ed.SelectionStart, e = ed.SelectionEnd;
                if (e > s && e - s < 200)
                {
                    string sel = ed.GetTextRange(s, e);
                    if (sel.IndexOf('\n') < 0 && sel.IndexOf('\r') < 0) { findBox.Tag = "skip"; findBox.Text = sel; findBox.Tag = null; }
                }
            }
            replaceRow.Visible = replace;
            findBar.Visible = true;
            findMsg.Text = "";
            if (replace && findBox.TextLength > 0) { replaceBox.Focus(); replaceBox.SelectAll(); }
            else { findBox.Focus(); findBox.SelectAll(); }
        }

        void CloseFind()
        {
            findBar.Visible = false;
            if (Ed != null) Ed.Focus();
        }

        int SearchFlags()
        {
            int f = 0;
            if (chkCase.Checked) f |= Sci.SCFIND_MATCHCASE;
            if (chkWord.Checked) f |= Sci.SCFIND_WHOLEWORD;
            if (chkRegex.Checked) f |= Sci.SCFIND_REGEXP | Sci.SCFIND_CXX11REGEX;
            return f;
        }

        // Cerca in [start, end) (end < start = all'indietro). Restituisce false anche se la regex non è valida.
        bool Search(SciEditor ed, long start, long end, out bool badRegex)
        {
            ed.Send(Sci.SCI_SETSTATUS, 0);
            ed.Send(Sci.SCI_SETSEARCHFLAGS, SearchFlags());
            ed.Send(Sci.SCI_SETTARGETRANGE, start, end);
            long pos = ed.SendText(Sci.SCI_SEARCHINTARGET, findBox.Text);
            badRegex = ed.Send(Sci.SCI_GETSTATUS) != 0;
            ed.Send(Sci.SCI_SETSTATUS, 0);
            return pos >= 0 && !badRegex;
        }

        void SetFindMessage(string text, bool error)
        {
            findMsg.Text = text;
            findMsg.ForeColor = error ? theme.Error : theme.ChromeTextDim;
        }

        bool FindNext(bool forward, bool fromSelectionStart = false)
        {
            var ed = Ed;
            if (ed == null) return false;
            if (findBox.TextLength == 0) { ShowFind(replaceRow.Visible && findBar.Visible); return false; }
            long len = ed.Length;
            long start = forward ? (fromSelectionStart ? ed.SelectionStart : ed.SelectionEnd) : ed.SelectionStart;
            bool bad, wrapped = false;
            bool found = Search(ed, start, forward ? len : 0, out bad);
            if (found && forward && ed.Send(Sci.SCI_GETTARGETEND) == start && ed.Send(Sci.SCI_GETTARGETSTART) == start && !fromSelectionStart)
                found = start < len && Search(ed, start + 1, len, out bad); // corrispondenza vuota (regex): si va avanti
            if (!found && !bad)
            {
                wrapped = true;
                found = Search(ed, forward ? 0 : len, start, out bad);
            }
            if (bad) { SetFindMessage(L.T("Espressione regolare non valida", "Invalid regular expression"), true); return false; }
            if (!found) { SetFindMessage(L.T("Nessun risultato", "No results"), true); return false; }
            long ts = ed.Send(Sci.SCI_GETTARGETSTART), te = ed.Send(Sci.SCI_GETTARGETEND);
            ed.Send(Sci.SCI_ENSUREVISIBLEENFORCEPOLICY, ed.LineFromPosition(ts));
            ed.Send(Sci.SCI_SETSEL, ts, te);
            ed.Send(Sci.SCI_SCROLLCARET);
            SetFindMessage(wrapped ? (forward ? L.T("Ricominciato dall'inizio", "Wrapped to the top") : L.T("Ricominciato dalla fine", "Wrapped to the bottom")) : "", false);
            return true;
        }

        void IncrementalFind()
        {
            if (!findBar.Visible || findBox.Tag != null || chkRegex.Checked) { if (findBar.Visible) findMsg.Text = ""; return; }
            var ed = Ed;
            if (ed == null) return;
            if (findBox.TextLength == 0) { findMsg.Text = ""; ed.Send(Sci.SCI_SETEMPTYSELECTION, ed.SelectionStart); return; }
            FindNext(true, true);
        }

        // La selezione corrente corrisponde esattamente alla ricerca?
        bool SelectionMatches(SciEditor ed)
        {
            long s = ed.SelectionStart, e = ed.SelectionEnd;
            if (e <= s && !chkRegex.Checked) return false;
            bool bad;
            return Search(ed, s, e, out bad) && ed.Send(Sci.SCI_GETTARGETSTART) == s && ed.Send(Sci.SCI_GETTARGETEND) == e;
        }

        void ReplaceOne()
        {
            var ed = Ed;
            if (ed == null || findBox.TextLength == 0) return;
            if (SelectionMatches(ed))
            {
                long rl = ed.SendText(chkRegex.Checked ? Sci.SCI_REPLACETARGETRE : Sci.SCI_REPLACETARGET, replaceBox.Text);
                long ts = ed.Send(Sci.SCI_GETTARGETSTART);
                ed.Send(Sci.SCI_SETSEL, ts + rl, ts + rl);
            }
            FindNext(true);
        }

        void ReplaceAll()
        {
            var ed = Ed;
            if (ed == null || findBox.TextLength == 0) return;
            int count = 0;
            bool bad = false;
            Cursor = Cursors.WaitCursor;
            ed.Send(Sci.SCI_BEGINUNDOACTION);
            try
            {
                long pos = 0;
                int msg = chkRegex.Checked ? Sci.SCI_REPLACETARGETRE : Sci.SCI_REPLACETARGET;
                string repl = replaceBox.Text;
                while (pos <= ed.Length && Search(ed, pos, ed.Length, out bad))
                {
                    long ts = ed.Send(Sci.SCI_GETTARGETSTART), te = ed.Send(Sci.SCI_GETTARGETEND);
                    long rl = ed.SendText(msg, repl);
                    pos = ts + rl;
                    if (te == ts) pos++; // corrispondenza vuota
                    count++;
                }
            }
            finally
            {
                ed.Send(Sci.SCI_ENDUNDOACTION);
                Cursor = Cursors.Default;
            }
            if (bad) SetFindMessage(L.T("Espressione regolare non valida", "Invalid regular expression"), true);
            else SetFindMessage(count == 0 ? L.T("Nessun risultato", "No results") : L.F("Sostituiti: {0}", "Replaced: {0}", Util.N(count)), count == 0);
        }

        // ======================================================================= Stato

        void UpdateTitle()
        {
            var d = active;
            Text = d == null ? "AppPad" : (d.Dirty ? "*" : "") + d.Title + " - AppPad";
        }

        void UpdateStatus()
        {
            var d = active;
            sEnc.Text = d != null ? TextFile.Name(d.Encoding) : "";
            sEol.Text = d != null ? TextFile.EolName(d.Eol) : "";
            int pct = (int)Math.Round((fontSize + zoom) / fontSize * 100);
            sZoom.Text = Math.Max(10, pct) + "%";
            var ed = Ed;
            if (ed == null)
            {
                sPos.Text = d != null && d.Loading ? L.T("Caricamento...", "Loading...") : "";
                sSel.Text = sLines.Text = "";
                return;
            }
            long pos = ed.CurrentPos, line = ed.LineFromPosition(pos), col = ed.Send(Sci.SCI_GETCOLUMN, pos);
            sPos.Text = L.F("Rg {0}, col {1}", "Ln {0}, Col {1}", Util.N(line + 1), Util.N(col + 1));
            long s = ed.SelectionStart, e = ed.SelectionEnd;
            int sels = (int)ed.Send(Sci.SCI_GETSELECTIONS);
            if (sels > 1) sSel.Text = L.F("{0} selezioni", "{0} selections", sels);
            else if (e > s)
            {
                long chars = e - s <= 8 * 1024 * 1024 ? ed.Send(Sci.SCI_COUNTCHARACTERS, s, e) : e - s;
                sSel.Text = L.F("{0} selezionati", "{0} selected", Util.N(chars));
            }
            else sSel.Text = "";
            sLines.Text = L.F("{0} righe", "{0} lines", Util.N(ed.LineCount));
        }

        // ======================================================================= Tastiera

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown:
                    SwitchTab(1); return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp:
                    SwitchTab(-1); return true;
                case Keys.Control | Keys.Add:
                    SetZoom(zoom + 1); return true;
                case Keys.Control | Keys.Subtract:
                    SetZoom(zoom - 1); return true;
                case Keys.F1:
                    ToggleAiPanel(); return true;
                case Keys.Escape:
                    if (aiJob != null) { StopAi(); return true; }
                    if (aiChat != null) { StopChat(); return true; }
                    if (aiPanel.Visible && aiPanel.ContainsFocus) { HideAiPanel(); return true; }
                    if (findBar.Visible) { CloseFind(); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void SwitchTab(int dir)
        {
            if (Docs.Count < 2 || active == null) return;
            int i = Docs.IndexOf(active);
            Activate(Docs[(i + dir + Docs.Count) % Docs.Count]);
        }

        // ======================================================================= Avvio e chiusura

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            loaderFactory = new SciEditor { Visible = false };
            Controls.Add(loaderFactory);
            var h = loaderFactory.Handle;
            ApplyTheme();

            int activeIdx = 0;
            if (primary)
            {
                foreach (var d in Session.Load(out activeIdx)) Docs.Add(d);
            }
            if (Docs.Count > 0) Activate(Docs[Math.Min(activeIdx, Docs.Count - 1)]);
            if (startFiles.Count > 0) OpenFiles(startFiles);
            if (Docs.Count == 0) NewDoc();

            if (primary)
            {
                SingleInstance.Listen(files =>
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                            Activate();
                            if (files.Count > 0) OpenFiles(files);
                        }));
                    }
                    catch { }
                });
                sessionTimer.Start();
            }
        }

        void SaveSession(bool full)
        {
            sessionDirty = false;
            string err;
            Session.Save(Docs, active, full, out err);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            AbortAi(null);   // il testo sotto l'assistente torna com'era prima di salvare la sessione
            StopChat();
            if (primary)
            {
                // La sessione conserva anche il testo non salvato: non serve chiedere niente
                string err;
                if (!Session.Save(Docs, active, true, out err) && e.CloseReason == CloseReason.UserClosing)
                {
                    if (MessageBox.Show(this, L.F("Non è stato possibile salvare la sessione:\n{0}\n\nChiudere comunque? Le modifiche non salvate andranno perse.",
                            "Could not save the session:\n{0}\n\nClose anyway? Unsaved changes will be lost.", err),
                            "AppPad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
                }
            }
            else
            {
                // Seconda finestra (raro): niente sessione, si chiede come in Blocco note
                foreach (var d in Docs.ToList())
                {
                    if (!d.Loaded || !d.Dirty) continue;
                    Activate(d);
                    var r = MessageBox.Show(this, L.F("Salvare le modifiche a {0}?", "Save changes to {0}?", d.Path ?? d.Title), "AppPad",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel || (r == DialogResult.Yes && !SaveDoc(d, false))) { e.Cancel = true; return; }
                }
            }
            closing = true;
            foreach (var d in Docs) if (d.Job != null) d.Job.Cancel = true;
            SaveBounds();
            Settings.Save();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            LocalAi.Instance.Stop();
            if (primary) Updater.InstallOnExit();   // aggiornamento scaricato e verificato: si installa adesso, in silenzio
            base.OnFormClosed(e);
        }

        void RestoreBounds_()
        {
            var p = Settings.Get("window", "").Split(',');
            int x, y, w, h;
            if (p.Length >= 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h) && w > 200 && h > 150)
            {
                var r = new Rectangle(x, y, w, h);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { Bounds = r; }
            }
            if (p.Length >= 5 && p[4] == "max") WindowState = FormWindowState.Maximized;
        }

        void SaveBounds()
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.Set("window", r.X + "," + r.Y + "," + r.Width + "," + r.Height + (WindowState == FormWindowState.Maximized ? ",max" : ""));
        }
    }
}
