// Controllo editor basato su Scintilla (https://www.scintilla.org), lo stesso motore di Notepad++.
// Scintilla.dll è incorporata nell'exe: al primo avvio viene estratta in %LOCALAPPDATA%\AppPad\bin\<hash>\.
// Si comanda con messaggi SCI_* chiamati tramite la "funzione diretta" (più veloce di SendMessage).
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace AppPad
{
    // Costanti da Scintilla.h (versione 5.6.9)
    static class Sci
    {
        public const int SCI_SETTEXT = 2181, SCI_GETLENGTH = 2006, SCI_GETTEXTLENGTH = 2183, SCI_CLEARALL = 2004;
        public const int SCI_GETCURRENTPOS = 2008, SCI_GETANCHOR = 2009, SCI_SETSEL = 2160, SCI_GOTOPOS = 2025, SCI_GOTOLINE = 2024;
        public const int SCI_SETEMPTYSELECTION = 2556, SCI_GETSELECTIONSTART = 2143, SCI_GETSELECTIONEND = 2145, SCI_GETSELECTIONEMPTY = 2650;
        public const int SCI_GETSELECTIONS = 2570, SCI_GETSELTEXT = 2161, SCI_REPLACESEL = 2170, SCI_SELECTALL = 2013;
        public const int SCI_LINEFROMPOSITION = 2166, SCI_POSITIONFROMLINE = 2167, SCI_GETCOLUMN = 2129, SCI_GETLINECOUNT = 2154;
        public const int SCI_COUNTCHARACTERS = 2633, SCI_GETLINEENDPOSITION = 2136, SCI_GETTEXTRANGEFULL = 2039;
        public const int SCI_POSITIONRELATIVE = 2670;
        public const int SCI_UNDO = 2176, SCI_REDO = 2011, SCI_CUT = 2177, SCI_COPY = 2178, SCI_PASTE = 2179, SCI_CLEAR = 2180;
        public const int SCI_CANUNDO = 2174, SCI_CANREDO = 2016, SCI_EMPTYUNDOBUFFER = 2175, SCI_SETUNDOCOLLECTION = 2012;
        public const int SCI_BEGINUNDOACTION = 2078, SCI_ENDUNDOACTION = 2079;
        public const int SCI_INSERTTEXT = 2003, SCI_DELETERANGE = 2645;
        public const int SCI_SETSAVEPOINT = 2014, SCI_GETMODIFY = 2159, SCI_SETREADONLY = 2171, SCI_GETREADONLY = 2140;
        public const int SCI_SETCODEPAGE = 2037, SCI_SETEOLMODE = 2031, SCI_GETEOLMODE = 2030, SCI_CONVERTEOLS = 2029;
        public const int SCI_SETVIEWEOL = 2356, SCI_SETVIEWWS = 2021, SCI_SETWRAPMODE = 2268, SCI_SETTABWIDTH = 2036, SCI_SETUSETABS = 2124;
        public const int SCI_SETMARGINTYPEN = 2240, SCI_SETMARGINWIDTHN = 2242, SCI_SETMARGINLEFT = 2155, SCI_SETMARGINRIGHT = 2157, SCI_SETMARGINS = 2252;
        public const int SCI_STYLESETFORE = 2051, SCI_STYLESETBACK = 2052, SCI_STYLESETFONT = 2056, SCI_STYLESETSIZEFRACTIONAL = 2061, SCI_STYLECLEARALL = 2050;
        public const int SCI_SETELEMENTCOLOUR = 2753, SCI_SETCARETLINEVISIBLEALWAYS = 2655, SCI_SETCARETWIDTH = 2188;
        public const int SCI_SETWHITESPACESIZE = 2086, SCI_SETEXTRAASCENT = 2525, SCI_SETEXTRADESCENT = 2527;
        public const int SCI_SETTECHNOLOGY = 2630, SCI_SETLAYOUTCACHE = 2272, SCI_SETLAYOUTTHREADS = 2775, SCI_SETFONTQUALITY = 2611;
        public const int SCI_SETZOOM = 2373, SCI_GETZOOM = 2374, SCI_TEXTWIDTH = 2276;
        public const int SCI_GETFIRSTVISIBLELINE = 2152, SCI_SETFIRSTVISIBLELINE = 2613, SCI_SCROLLCARET = 2169, SCI_CHOOSECARETX = 2399;
        public const int SCI_SETXCARETPOLICY = 2402, SCI_SETYCARETPOLICY = 2403, SCI_ENSUREVISIBLEENFORCEPOLICY = 2234;
        public const int SCI_SETSCROLLWIDTH = 2274, SCI_SETSCROLLWIDTHTRACKING = 2516, SCI_SETENDATLASTLINE = 2277;
        public const int SCI_SETTARGETRANGE = 2686, SCI_GETTARGETSTART = 2191, SCI_GETTARGETEND = 2193, SCI_SETSEARCHFLAGS = 2198;
        public const int SCI_SEARCHINTARGET = 2197, SCI_REPLACETARGET = 2194, SCI_REPLACETARGETRE = 2195;
        public const int SCI_GETCHARACTERPOINTER = 2520, SCI_CREATELOADER = 2632, SCI_CREATEDOCUMENT = 2375;
        public const int SCI_SETDOCPOINTER = 2358, SCI_RELEASEDOCUMENT = 2377;
        public const int SCI_GETDIRECTFUNCTION = 2184, SCI_GETDIRECTPOINTER = 2185, SCI_SETMODEVENTMASK = 2359, SCI_USEPOPUP = 2371;
        public const int SCI_SETMULTIPLESELECTION = 2563, SCI_SETADDITIONALSELECTIONTYPING = 2565, SCI_SETMULTIPASTE = 2614;
        public const int SCI_SETVIRTUALSPACEOPTIONS = 2596, SCI_SETMOUSESELECTIONRECTANGULARSWITCH = 2668, SCI_SETPASTECONVERTENDINGS = 2467;
        public const int SCI_UPPERCASE = 2341, SCI_LOWERCASE = 2340, SCI_SELECTIONDUPLICATE = 2469, SCI_LINEDELETE = 2338;
        public const int SCI_INDICSETSTYLE = 2080, SCI_INDICSETFORE = 2082, SCI_INDICSETALPHA = 2523, SCI_INDICSETOUTLINEALPHA = 2558, SCI_INDICSETUNDER = 2510;
        public const int SCI_SETINDICATORCURRENT = 2500, SCI_INDICATORFILLRANGE = 2504, SCI_INDICATORCLEARRANGE = 2505;
        public const int SCI_SETSTATUS = 2382, SCI_GETSTATUS = 2383, SCI_SETCHARACTERCATEGORYOPTIMIZATION = 2720;
        public const int SCI_CHARPOSITIONFROMPOINTCLOSE = 2562;

        public const int SC_EOL_CRLF = 0, SC_EOL_CR = 1, SC_EOL_LF = 2;
        public const int SC_CP_UTF8 = 65001, SC_MARGIN_NUMBER = 1;
        public const int SC_WRAP_NONE = 0, SC_WRAP_WORD = 1;
        public const int SC_CACHE_PAGE = 2, SC_TECHNOLOGY_DIRECTWRITE = 1, SC_EFF_QUALITY_LCD_OPTIMIZED = 3;
        public const int STYLE_DEFAULT = 32, STYLE_LINENUMBER = 33, STYLE_CONTROLCHAR = 36;
        public const int SC_ELEMENT_SELECTION_BACK = 11, SC_ELEMENT_SELECTION_ADDITIONAL_BACK = 13, SC_ELEMENT_SELECTION_INACTIVE_BACK = 17;
        public const int SC_ELEMENT_SELECTION_INACTIVE_ADDITIONAL_BACK = 19;
        public const int SC_ELEMENT_CARET = 40, SC_ELEMENT_CARET_ADDITIONAL = 41, SC_ELEMENT_CARET_LINE_BACK = 50, SC_ELEMENT_WHITE_SPACE = 60;
        public const int SC_DOCUMENTOPTION_STYLES_NONE = 0x1, SC_DOCUMENTOPTION_TEXT_LARGE = 0x100;
        public const int SC_MOD_INSERTTEXT = 0x1, SC_MOD_DELETETEXT = 0x2;
        public const int SC_MULTIPASTE_EACH = 1, SCVS_RECTANGULARSELECTION = 1;
        public const int SCFIND_WHOLEWORD = 0x2, SCFIND_MATCHCASE = 0x4, SCFIND_REGEXP = 0x00200000, SCFIND_CXX11REGEX = 0x00800000;
        public const int CARET_SLOP = 0x01, CARET_STRICT = 0x04, CARET_EVEN = 0x08;
        public const int INDIC_ROUNDBOX = 7;
        public const int SC_UPDATE_CONTENT = 0x1, SC_UPDATE_SELECTION = 0x2;
        public const int SC_STATUS_OK = 0;

        public const int SCN_SAVEPOINTREACHED = 2002, SCN_SAVEPOINTLEFT = 2003, SCN_UPDATEUI = 2007, SCN_MODIFIED = 2008, SCN_ZOOM = 2018;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SCNotification
    {
        public IntPtr hwndFrom, idFrom; public int code;
        public IntPtr position; public int ch, modifiers, modificationType;
        public IntPtr text, length, linesAdded; public int message; public IntPtr wParam, lParam, line;
        public int foldLevelNow, foldLevelPrev, margin, listType, x, y, token;
        public IntPtr annotationLinesAdded; public int updated, listCompletionMethod, characterSource;
    }

    // Estrae e carica Scintilla.dll una volta sola per processo.
    static class SciLib
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        static bool loaded;

        public static void Load()
        {
            if (loaded) return;
            byte[] data;
            using (var st = typeof(SciLib).Assembly.GetManifestResourceStream("AppPad.Scintilla.dll"))
            {
                if (st == null) throw new FileNotFoundException("Scintilla.dll non incorporata nell'exe");
                data = new byte[st.Length];
                int off = 0, n;
                while (off < data.Length && (n = st.Read(data, off, data.Length - off)) > 0) off += n;
            }
            string hash = Hash(data);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPad", "bin", hash.Substring(0, 16));
            string file = Path.Combine(dir, "Scintilla.dll");
            bool ok = false;
            try { ok = File.Exists(file) && Hash(File.ReadAllBytes(file)) == hash; } catch { }
            if (!ok)
            {
                Directory.CreateDirectory(dir);
                string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
                catch
                {
                    // Un'altra istanza l'ha appena scritta: va bene se è identica
                    try { File.Delete(tmp); } catch { }
                    if (Hash(File.ReadAllBytes(file)) != hash) throw;
                }
            }
            if (LoadLibrary(file) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Scintilla.dll");
            loaded = true;
        }

        static string Hash(byte[] d)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
        }
    }

    // Caricamento di un documento fuori dal thread dell'interfaccia (interfaccia ILoader di Scintilla).
    // Si crea sul thread UI con SciEditor.CreateLoader, AddData/ConvertToDocument possono essere chiamati da un altro thread.
    sealed class SciLoader
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddDataFn(IntPtr self, IntPtr data, IntPtr length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr ConvertFn(IntPtr self);

        IntPtr self;
        readonly ReleaseFn release; readonly AddDataFn addData; readonly ConvertFn convert;

        public SciLoader(IntPtr loader)
        {
            self = loader;
            IntPtr vt = Marshal.ReadIntPtr(loader);
            release = (ReleaseFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 0), typeof(ReleaseFn));
            addData = (AddDataFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, IntPtr.Size), typeof(AddDataFn));
            convert = (ConvertFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 2 * IntPtr.Size), typeof(ConvertFn));
        }

        public unsafe int AddData(byte[] buf, int offset, int count)
        {
            if (count <= 0) return Sci.SC_STATUS_OK;
            fixed (byte* p = buf) return addData(self, (IntPtr)(p + offset), (IntPtr)count);
        }

        // Restituisce il documento (con un riferimento già preso) e invalida il loader
        public IntPtr ToDocument() { IntPtr d = convert(self); self = IntPtr.Zero; return d; }

        public void Release() { if (self != IntPtr.Zero) { release(self); self = IntPtr.Zero; } }
    }

    class SciEditor : Control
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr DirectFn(IntPtr ptr, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        DirectFn fn;
        IntPtr ptr;
        FileDropTarget dropTarget;

        public event Action<SciEditor, SCNotification> Notify;
        public event Action<string[]> FilesDropped;

        public SciEditor()
        {
            SciLib.Load();
            SetStyle(ControlStyles.UserPaint | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.UseTextForAccessibility, false);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassName = "Scintilla"; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            fn = (DirectFn)Marshal.GetDelegateForFunctionPointer(SendMessage(Handle, Sci.SCI_GETDIRECTFUNCTION, IntPtr.Zero, IntPtr.Zero), typeof(DirectFn));
            ptr = SendMessage(Handle, Sci.SCI_GETDIRECTPOINTER, IntPtr.Zero, IntPtr.Zero);
            dropTarget = FileDropTarget.Install(Handle, files => { if (FilesDropped != null) FilesDropped(files); });
            base.OnHandleCreated(e);
        }

        public void SetDarkScrollbars(bool dark)
        {
            if (IsHandleCreated) try { SetWindowTheme(Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x204E: // WM_REFLECT + WM_NOTIFY: le notifiche SCN_* che WinForms rimanda al controllo
                    if (Notify != null) Notify(this, (SCNotification)Marshal.PtrToStructure(m.LParam, typeof(SCNotification)));
                    return;
                case 0x2111: // WM_REFLECT + WM_COMMAND (SCEN_CHANGE ecc.): non servono
                    return;
                case 0x0020: // WM_SETCURSOR: lascia a Scintilla il cursore a I
                    DefWndProc(ref m);
                    return;
                case 0x000D: // WM_GETTEXT
                case 0x000E: // WM_GETTEXTLENGTH
                    // Windows (accessibilità, servizi di testo) chiede il "nome" della finestra con WM_GETTEXT:
                    // Scintilla risponderebbe con tutto il documento convertito in UTF-16, cioè centinaia di MB
                    // per un file grande. Il testo si legge sempre con i messaggi SCI_*.
                    if (m.Msg == 0x000D && m.WParam != IntPtr.Zero && m.LParam != IntPtr.Zero) Marshal.WriteInt16(m.LParam, 0);
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }

        // ---- Chiamate ----
        public long Send(int msg, long w = 0, long l = 0) { return (long)fn(ptr, msg, (IntPtr)w, (IntPtr)l); }
        public IntPtr SendPtr(int msg, long w, IntPtr l) { return fn(ptr, msg, (IntPtr)w, l); }

        public unsafe long SendStr(int msg, long w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes((s ?? "") + "\0");
            fixed (byte* p = b) return (long)fn(ptr, msg, (IntPtr)w, (IntPtr)p);
        }

        // Testo con lunghezza esplicita (SCI_SEARCHINTARGET, SCI_REPLACETARGET...)
        public unsafe long SendText(int msg, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length == 0) return (long)fn(ptr, msg, IntPtr.Zero, (IntPtr)(&msg));
            fixed (byte* p = b) return (long)fn(ptr, msg, (IntPtr)b.Length, (IntPtr)p);
        }

        public static int Rgb(System.Drawing.Color c) { return c.R | (c.G << 8) | (c.B << 16); }
        public static int Rgba(System.Drawing.Color c) { return Rgb(c) | (c.A << 24); }

        // ---- Proprietà comode ----
        public long Length { get { return Send(Sci.SCI_GETLENGTH); } }
        public long CurrentPos { get { return Send(Sci.SCI_GETCURRENTPOS); } }
        public long AnchorPos { get { return Send(Sci.SCI_GETANCHOR); } }
        public long SelectionStart { get { return Send(Sci.SCI_GETSELECTIONSTART); } }
        public long SelectionEnd { get { return Send(Sci.SCI_GETSELECTIONEND); } }
        public bool Modified { get { return Send(Sci.SCI_GETMODIFY) != 0; } }
        public long LineCount { get { return Send(Sci.SCI_GETLINECOUNT); } }
        public bool ReadOnly { get { return Send(Sci.SCI_GETREADONLY) != 0; } set { Send(Sci.SCI_SETREADONLY, value ? 1 : 0); } }
        public long LineFromPosition(long pos) { return Send(Sci.SCI_LINEFROMPOSITION, pos); }

        public unsafe string GetTextRange(long start, long end)
        {
            if (end <= start) return "";
            var buf = new byte[end - start + 1];
            fixed (byte* p = buf)
            {
                var tr = new IntPtr[] { (IntPtr)start, (IntPtr)end, (IntPtr)p };
                fixed (IntPtr* t = tr) fn(ptr, Sci.SCI_GETTEXTRANGEFULL, IntPtr.Zero, (IntPtr)t);
            }
            return Encoding.UTF8.GetString(buf, 0, buf.Length - 1);
        }

        public string SelectedText
        {
            get
            {
                long s = SelectionStart, e = SelectionEnd;
                return e - s > 50 * 1024 * 1024 ? "" : GetTextRange(s, e);
            }
        }

        public string Text_
        {
            get { return GetTextRange(0, Length); }
            set { SendStr(Sci.SCI_SETTEXT, 0, value); }
        }

        // Puntatore al testo come blocco unico (valido finché il documento non cambia)
        public IntPtr CharacterPointer { get { return SendPtr(Sci.SCI_GETCHARACTERPOINTER, 0, IntPtr.Zero); } }

        public SciLoader CreateLoader(long bytes, bool large)
        {
            int opts = Sci.SC_DOCUMENTOPTION_STYLES_NONE | (large ? Sci.SC_DOCUMENTOPTION_TEXT_LARGE : 0);
            IntPtr p = SendPtr(Sci.SCI_CREATELOADER, bytes, (IntPtr)opts);
            return p == IntPtr.Zero ? null : new SciLoader(p);
        }

        // Sostituisce il documento mostrato (il riferimento preso da CreateDocument/loader passa all'editor)
        public void AttachDocument(IntPtr doc)
        {
            SendPtr(Sci.SCI_SETDOCPOINTER, 0, doc);
            SendPtr(Sci.SCI_RELEASEDOCUMENT, 0, doc);
        }

        public void NewEmptyDocument()
        {
            AttachDocument(SendPtr(Sci.SCI_CREATEDOCUMENT, 0, (IntPtr)Sci.SC_DOCUMENTOPTION_STYLES_NONE));
        }
    }

    // Trascinamento: i file trascinati sull'editor vengono aperti, il testo resta gestito da Scintilla.
    // Scintilla registra il proprio IDropTarget, che rifiuta i file: lo sostituiamo con uno che inoltra a quello
    // originale tutto ciò che non è un elenco di file. Il puntatore originale si usa senza mai rilasciarlo
    // (appartiene alla finestra di Scintilla e vive quanto lei).
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr pDataObj, int grfKeyState, long pt, ref int pdwEffect);
        [PreserveSig] int DragOver(int grfKeyState, long pt, ref int pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr pDataObj, int grfKeyState, long pt, ref int pdwEffect);
    }

    [ComVisible(true)]
    sealed class FileDropTarget : IOleDropTarget
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);
        [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr h);
        [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr h, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget t);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int DragQueryFile(IntPtr hDrop, int i, StringBuilder f, int cch);
        [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM m);

        delegate int EnterFn(IntPtr self, IntPtr data, int key, long pt, ref int eff);
        delegate int OverFn(IntPtr self, int key, long pt, ref int eff);
        delegate int LeaveFn(IntPtr self);

        IntPtr inner;
        EnterFn innerEnter, innerDrop; OverFn innerOver; LeaveFn innerLeave;
        Action<string[]> onFiles;
        bool files;
        const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, CF_HDROP = 15;

        public static FileDropTarget Install(IntPtr hwnd, Action<string[]> onFiles)
        {
            try
            {
                var t = new FileDropTarget { onFiles = onFiles, inner = GetProp(hwnd, "OleDropTargetInterface") };
                if (t.inner != IntPtr.Zero)
                {
                    IntPtr vt = Marshal.ReadIntPtr(t.inner);
                    t.innerEnter = (EnterFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 3 * IntPtr.Size), typeof(EnterFn));
                    t.innerOver = (OverFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 4 * IntPtr.Size), typeof(OverFn));
                    t.innerLeave = (LeaveFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 5 * IntPtr.Size), typeof(LeaveFn));
                    t.innerDrop = (EnterFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, 6 * IntPtr.Size), typeof(EnterFn));
                }
                RevokeDragDrop(hwnd);
                return RegisterDragDrop(hwnd, t) == 0 ? t : null;
            }
            catch { return null; }
        }

        static System.Runtime.InteropServices.ComTypes.FORMATETC HDrop()
        {
            return new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = CF_HDROP, dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
            };
        }

        static bool HasFiles(IntPtr data)
        {
            if (data == IntPtr.Zero) return false;
            try
            {
                var d = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(data);
                var f = HDrop();
                return d.QueryGetData(ref f) == 0;
            }
            catch { return false; }
        }

        static string[] GetFiles(IntPtr data)
        {
            try
            {
                var d = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(data);
                var f = HDrop();
                System.Runtime.InteropServices.ComTypes.STGMEDIUM m;
                d.GetData(ref f, out m);
                try
                {
                    int n = DragQueryFile(m.unionmember, -1, null, 0);
                    var list = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        var sb = new StringBuilder(DragQueryFile(m.unionmember, i, null, 0) + 1);
                        DragQueryFile(m.unionmember, i, sb, sb.Capacity);
                        list[i] = sb.ToString();
                    }
                    return list;
                }
                finally { ReleaseStgMedium(ref m); }
            }
            catch { return new string[0]; }
        }

        public int DragEnter(IntPtr data, int key, long pt, ref int eff)
        {
            files = HasFiles(data);
            if (files) { eff &= DROPEFFECT_COPY; return 0; }
            if (innerEnter != null) return innerEnter(inner, data, key, pt, ref eff);
            eff = DROPEFFECT_NONE; return 0;
        }

        public int DragOver(int key, long pt, ref int eff)
        {
            if (files) { eff &= DROPEFFECT_COPY; return 0; }
            if (innerOver != null) return innerOver(inner, key, pt, ref eff);
            eff = DROPEFFECT_NONE; return 0;
        }

        public int DragLeave()
        {
            if (!files && innerLeave != null) return innerLeave(inner);
            return 0;
        }

        public int Drop(IntPtr data, int key, long pt, ref int eff)
        {
            if (files)
            {
                files = false;
                eff &= DROPEFFECT_COPY;
                var list = GetFiles(data);
                if (list.Length > 0 && onFiles != null) onFiles(list);
                return 0;
            }
            if (innerDrop != null) return innerDrop(inner, data, key, pt, ref eff);
            eff = DROPEFFECT_NONE; return 0;
        }
    }
}
