// Documenti aperti, lettura/scrittura dei file (codifiche, fine riga) e sessione ripristinata all'avvio.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AppPad
{
    enum Enc { Utf8, Utf8Bom, Utf16LE, Utf16BE, Ansi }

    class Doc
    {
        public string Path;             // null = senza titolo
        public int UntitledNo;
        public Enc Encoding = Enc.Utf8;
        public int Eol = Sci.SC_EOL_CRLF;
        public SciEditor Editor;        // null finché la scheda non viene mostrata (sessione ripristinata)
        public LoadJob Job;             // caricamento in corso
        public bool ExtraDirty;         // modifiche non viste da Scintilla: ripristinato da backup, codifica cambiata
        public bool ChangedSinceBackup;
        public string BackupId;         // file in %LOCALAPPDATA%\AppPad\backup
        public DateTime FileTimeUtc;
        public long FileSize = -1;
        public long RestoreCaret = -1, RestoreAnchor = -1, RestoreFirstLine = -1;
        public int MarginDigits;
        public bool CloseAfterLoad;
        public bool Closed;

        public bool Loading { get { return Job != null; } }
        public bool Loaded { get { return Editor != null && Job == null; } }
        public bool Dirty { get { return ExtraDirty || (Loaded && Editor.Modified); } }

        public string Title
        {
            get { return Path != null ? System.IO.Path.GetFileName(Path) : L.F("Senza titolo {0}", "Untitled {0}", UntitledNo); }
        }

        public string BackupFile { get { return BackupId == null ? null : System.IO.Path.Combine(Session.BackupDir, BackupId + ".txt"); } }
    }

    class LoadJob
    {
        public string Source;
        public Enc? Forced;
        public volatile bool Cancel;
        public long Total, Done;
        // risultati
        public Enc Encoding;
        public int Eol = -1;
        public IntPtr Document;
        public Exception Error;
        public bool Cancelled;
    }

    static class TextFile
    {
        public const int Chunk = 4 << 20;

        public static Encoding GetEncoding(Enc e)
        {
            switch (e)
            {
                case Enc.Utf8Bom: return new UTF8Encoding(true);
                case Enc.Utf16LE: return new UnicodeEncoding(false, true);
                case Enc.Utf16BE: return new UnicodeEncoding(true, true);
                case Enc.Ansi: return Encoding.Default;
                default: return new UTF8Encoding(false);
            }
        }

        public static string Name(Enc e)
        {
            switch (e)
            {
                case Enc.Utf8Bom: return L.T("UTF-8 con BOM", "UTF-8 with BOM");
                case Enc.Utf16LE: return "UTF-16 LE";
                case Enc.Utf16BE: return "UTF-16 BE";
                case Enc.Ansi: return "ANSI (" + Encoding.Default.WebName + ")";
                default: return "UTF-8";
            }
        }

        public static string EolName(int eol)
        {
            return eol == Sci.SC_EOL_LF ? "Unix (LF)" : eol == Sci.SC_EOL_CR ? "Mac (CR)" : "Windows (CRLF)";
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int off = 0, n;
            while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
            return off;
        }

        // Codifica dal BOM o, in mancanza, dal contenuto del primo blocco
        public static Enc Detect(byte[] b, int n, out int bom)
        {
            bom = 0;
            if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { bom = 3; return Enc.Utf8Bom; }
            if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE) { bom = 2; return Enc.Utf16LE; }
            if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF) { bom = 2; return Enc.Utf16BE; }

            // UTF-16 senza BOM: tanti zeri in una sola delle due posizioni
            int sample = Math.Min(n, 64 * 1024) & ~1, zEven = 0, zOdd = 0;
            for (int i = 0; i < sample; i += 2) { if (b[i] == 0) zEven++; if (b[i + 1] == 0) zOdd++; }
            int pairs = sample / 2;
            if (pairs >= 4)
            {
                if (zOdd > pairs * 4 / 10 && zEven < pairs / 20) return Enc.Utf16LE;
                if (zEven > pairs * 4 / 10 && zOdd < pairs / 20) return Enc.Utf16BE;
            }
            return IsUtf8(b, n) ? Enc.Utf8 : Enc.Ansi;
        }

        // UTF-8 valido (una sequenza troncata alla fine del blocco è accettata)
        static bool IsUtf8(byte[] b, int n)
        {
            int i = 0;
            while (i < n)
            {
                byte c = b[i];
                if (c < 0x80) { i++; continue; }
                int len;
                if (c >= 0xC2 && c <= 0xDF) len = 2;
                else if (c >= 0xE0 && c <= 0xEF) len = 3;
                else if (c >= 0xF0 && c <= 0xF4) len = 4;
                else return false;
                if (i + len > n) return n - i < 4 && n == Chunk; // troncata dal blocco
                for (int k = 1; k < len; k++) if ((b[i + k] & 0xC0) != 0x80) return false;
                if (len == 3 && c == 0xE0 && b[i + 1] < 0xA0) return false;
                if (len == 3 && c == 0xED && b[i + 1] > 0x9F) return false;
                if (len == 4 && c == 0xF0 && b[i + 1] < 0x90) return false;
                if (len == 4 && c == 0xF4 && b[i + 1] > 0x8F) return false;
                i += len;
            }
            return true;
        }

        static int DetectEol(byte[] b, int off, int n)
        {
            int end = off + n;
            for (int i = off; i < end; i++)
            {
                if (b[i] == '\n') return Sci.SC_EOL_LF;
                if (b[i] == '\r')
                {
                    if (i + 1 >= end) return -1; // non si sa ancora
                    return b[i + 1] == '\n' ? Sci.SC_EOL_CRLF : Sci.SC_EOL_CR;
                }
            }
            return -1;
        }

        // Eseguito su un thread in background: legge il file e lo passa a Scintilla convertito in UTF-8.
        public static void Load(LoadJob job, SciLoader loader)
        {
            try
            {
                using (var fs = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                {
                    job.Total = fs.Length;
                    var buf = new byte[Chunk];
                    int n = ReadFull(fs, buf), bom;
                    Enc det = Detect(buf, n, out bom);
                    Enc enc = job.Forced ?? det;
                    int off = 0;
                    if (enc == det || (enc == Enc.Utf8 && det == Enc.Utf8Bom) || (enc == Enc.Utf8Bom && det == Enc.Utf8Bom)) off = bom;
                    if (enc == Enc.Utf8 && det == Enc.Utf8Bom) enc = Enc.Utf8Bom;
                    job.Encoding = enc;

                    Decoder dec = null; Encoder u8 = null; char[] cbuf = null; byte[] obuf = null;
                    if (enc != Enc.Utf8 && enc != Enc.Utf8Bom)
                    {
                        dec = GetEncoding(enc).GetDecoder();
                        u8 = new UTF8Encoding(false).GetEncoder();
                        cbuf = new char[Chunk + 4];
                        obuf = new byte[(Chunk + 4) * 3];
                    }
                    while (n > 0)
                    {
                        if (job.Cancel) { job.Cancelled = true; loader.Release(); return; }
                        byte[] ob = buf; int oo = off, on = n - off;
                        if (dec != null)
                        {
                            int cc = dec.GetChars(buf, off, n - off, cbuf, 0, false);
                            on = u8.GetBytes(cbuf, 0, cc, obuf, 0, false); ob = obuf; oo = 0;
                        }
                        if (job.Eol < 0) job.Eol = DetectEol(ob, oo, on);
                        if (loader.AddData(ob, oo, on) != Sci.SC_STATUS_OK) throw new OutOfMemoryException();
                        job.Done += n;
                        off = 0;
                        n = ReadFull(fs, buf);
                    }
                    if (dec != null)
                    {
                        int cc = dec.GetChars(new byte[0], 0, 0, cbuf, 0, true);
                        int on = u8.GetBytes(cbuf, 0, cc, obuf, 0, true);
                        if (loader.AddData(obuf, 0, on) != Sci.SC_STATUS_OK) throw new OutOfMemoryException();
                    }
                }
                if (job.Eol < 0) job.Eol = Sci.SC_EOL_CRLF;
                job.Document = loader.ToDocument();
            }
            catch (Exception ex)
            {
                job.Error = ex;
                loader.Release();
            }
        }

        // Salva il contenuto dell'editor. Scrive prima un file temporaneo nella stessa cartella e poi sostituisce
        // l'originale, così un errore a metà (disco pieno...) non rovina il file esistente.
        // lossy=false: EncoderFallbackException se un carattere non è rappresentabile nella codifica.
        public static void Save(SciEditor ed, string path, Enc enc, bool lossy)
        {
            IntPtr text = ed.CharacterPointer;
            long len = ed.Length;
            string dir = System.IO.Path.GetDirectoryName(path);
            string tmp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            FileStream fs;
            try { fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16); }
            catch (UnauthorizedAccessException)
            {
                // Cartella non scrivibile ma file sì: si scrive direttamente
                using (var direct = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16)) Write(direct, text, len, enc, lossy);
                return;
            }
            try
            {
                using (fs) Write(fs, text, len, enc, lossy);
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null, true); }
                    catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
                }
                else File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }

        static unsafe void Write(Stream fs, IntPtr text, long len, Enc enc, bool lossy)
        {
            byte* src = (byte*)text;
            if (enc == Enc.Utf8 || enc == Enc.Utf8Bom)
            {
                if (enc == Enc.Utf8Bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                using (var ums = new UnmanagedMemoryStream(src, len)) ums.CopyTo(fs, 1 << 20);
                return;
            }
            var target = (Encoding)GetEncoding(enc).Clone();
            target.EncoderFallback = lossy ? EncoderFallback.ReplacementFallback : EncoderFallback.ExceptionFallback;
            var pre = target.GetPreamble();
            fs.Write(pre, 0, pre.Length);
            var dec = new UTF8Encoding(false, false).GetDecoder();
            var en = target.GetEncoder();
            const int block = 1 << 20;
            var inb = new byte[block];
            var cb = new char[block + 4];
            var ob = new byte[target.GetMaxByteCount(block + 4)];
            for (long pos = 0; pos < len; pos += block)
            {
                int n = (int)Math.Min(block, len - pos);
                System.Runtime.InteropServices.Marshal.Copy((IntPtr)(src + pos), inb, 0, n);
                bool last = pos + n >= len;
                int cc = dec.GetChars(inb, 0, n, cb, 0, last);
                int bc = en.GetBytes(cb, 0, cc, ob, 0, last);
                fs.Write(ob, 0, bc);
            }
        }
    }

    // Sessione: %LOCALAPPDATA%\AppPad\session.txt con una riga per scheda; il testo non salvato
    // (file modificati e schede senza titolo) sta in %LOCALAPPDATA%\AppPad\backup\<id>.txt in UTF-8.
    static class Session
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPad");
        public static readonly string BackupDir = Path.Combine(Dir, "backup");
        static readonly string FilePath = Path.Combine(Dir, "session.txt");

        static string L_(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        static long P(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; }

        public static List<Doc> Load(out int active)
        {
            active = 0;
            var list = new List<Doc>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var ln in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = ln.Split('\t');
                    if (f[0] == "active" && f.Length > 1) active = (int)Math.Max(0, P(f[1]));
                    if (f[0] != "tab" || f.Length < 12) continue;
                    var d = new Doc
                    {
                        Path = f[1].Length > 0 ? f[1] : null,
                        UntitledNo = (int)Math.Max(0, P(f[2])),
                        BackupId = f[3].Length > 0 ? f[3] : null,
                        ExtraDirty = f[6] == "1",
                        RestoreCaret = P(f[7]), RestoreAnchor = P(f[8]), RestoreFirstLine = P(f[9]),
                        FileSize = P(f[11])
                    };
                    Enc e; if (Enum.TryParse(f[4], out e)) d.Encoding = e;
                    long eol = P(f[5]); if (eol >= 0 && eol <= 2) d.Eol = (int)eol;
                    long ticks = P(f[10]); if (ticks > 0) d.FileTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
                    // Un backup mancante non si può recuperare: si torna al file su disco
                    if (d.BackupId != null && !File.Exists(d.BackupFile)) { d.BackupId = null; d.ExtraDirty = false; }
                    if (d.ExtraDirty && d.BackupId == null) d.ExtraDirty = false;
                    if (d.Path == null && d.BackupId == null) continue;
                    if (d.Path != null && d.BackupId == null && !File.Exists(d.Path)) continue;
                    list.Add(d);
                }
                if (active >= list.Count) active = Math.Max(0, list.Count - 1);
            }
            catch { }
            return list;
        }

        // Scrive i backup dei documenti modificati e l'elenco delle schede.
        // full=false (salvataggio periodico): salta i documenti molto grandi per non bloccare l'interfaccia.
        public static bool Save(IList<Doc> docs, Doc active, bool full, out string error)
        {
            error = null;
            bool ok = true;
            Directory.CreateDirectory(BackupDir);
            var sb = new StringBuilder("AppPad session 1\r\n");
            int idx = 0, activeIdx = 0;
            foreach (var d in docs)
            {
                if (d.Loaded)
                {
                    bool needsBackup = d.Dirty;
                    if (d.Path == null && d.Editor.Length == 0) needsBackup = false;
                    if (needsBackup)
                    {
                        if (d.BackupId == null || d.ChangedSinceBackup || !File.Exists(d.BackupFile))
                        {
                            // Nel salvataggio periodico i documenti enormi si salvano solo alla chiusura
                            if (!full && d.Editor.Length > 64L * 1024 * 1024) { }
                            else
                            {
                                try
                                {
                                    if (d.BackupId == null) d.BackupId = Guid.NewGuid().ToString("N");
                                    TextFile.Save(d.Editor, d.BackupFile, Enc.Utf8, true);
                                    d.ChangedSinceBackup = false;
                                }
                                catch (Exception ex) { ok = false; error = d.Title + ": " + ex.Message; }
                            }
                        }
                    }
                    else d.BackupId = null;
                    if (d.Path == null && !needsBackup) continue; // scheda vuota senza titolo
                    d.RestoreCaret = d.Editor.CurrentPos;
                    d.RestoreAnchor = d.Editor.AnchorPos;
                    d.RestoreFirstLine = d.Editor.Send(Sci.SCI_GETFIRSTVISIBLELINE);
                }
                else if (d.Path == null && d.BackupId == null) continue;
                if (d == active) activeIdx = idx;
                bool dirty = d.Loaded ? d.Dirty : d.ExtraDirty;
                sb.Append("tab\t").Append(d.Path ?? "").Append('\t').Append(d.UntitledNo).Append('\t').Append(d.BackupId ?? "")
                  .Append('\t').Append(d.Encoding).Append('\t').Append(d.Eol).Append('\t').Append(dirty && d.BackupId != null ? "1" : "0")
                  .Append('\t').Append(L_(d.RestoreCaret)).Append('\t').Append(L_(d.RestoreAnchor)).Append('\t').Append(L_(d.RestoreFirstLine))
                  .Append('\t').Append(L_(d.FileTimeUtc.Ticks)).Append('\t').Append(L_(d.FileSize)).Append("\r\n");
                idx++;
            }
            sb.Append("active\t").Append(activeIdx).Append("\r\n");
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true); else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { ok = false; error = ex.Message; }

            // Backup non più usati da nessuna scheda
            try
            {
                var used = new HashSet<string>(docs.Where(d => d.BackupId != null).Select(d => d.BackupId + ".txt"), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(BackupDir, "*.txt"))
                    if (!used.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { }
            }
            catch { }
            return ok;
        }
    }
}
