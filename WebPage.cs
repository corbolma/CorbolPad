// Pagine web per il pannello dell'assistente: trova gli indirizzi nella domanda, scarica la pagina e ne ricava
// il testo leggibile (il contenuto principale, senza menu, script e stili).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CorbolPad
{
    class WebPage
    {
        public string Url;          // indirizzo finale, dopo i reindirizzamenti
        public string Title;
        public string Text;

        const int MaxBytes = 10 * 1024 * 1024;
        const int TimeoutMs = 30000;

        // ---- Indirizzi nella domanda ----

        static readonly Regex UrlRx = new Regex(@"\b(?:https?://|www\.)[^\s<>""«»“”]+", RegexOptions.IgnoreCase);

        public static List<string> FindUrls(string text)
        {
            var res = new List<string>();
            foreach (Match m in UrlRx.Matches(text ?? ""))
            {
                string u = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', ')', ']', '}');
                // Una parentesi chiusa fa parte dell'indirizzo solo se ce n'è una aperta (Wikipedia)
                if (m.Value.Length > u.Length && m.Value[u.Length] == ')' && u.Count(c => c == '(') > u.Count(c => c == ')')) u += ")";
                if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
                Uri uri;
                if (Uri.TryCreate(u, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host.Contains('.')
                    && !res.Contains(u))
                    res.Add(u);
            }
            return res;
        }

        public static string WithoutUrls(string text) { return UrlRx.Replace(text ?? "", " "); }

        // La domanda chiede solo di portare la pagina nell'editor ("trascrivi", "apri", o solo l'indirizzo), senza
        // riassunti, traduzioni o altre richieste: non serve l'assistente.
        static readonly string[] CopyWords = { "trascriv", "copia", "apri", "porta", "metti", "incolla", "scarica", "importa", "carica", "testo", "pagina", "scheda",
                                               "transcri", "copy", "open", "paste", "import", "fetch", "download", "bring", "load", "text", "page", "tab" };
        static readonly string[] TaskWords = { "riassu", "sintes", "sintet", "tradu", "spieg", "corregg", "analizz", "elenc", "estra", "confront", "rispond", "domand",
                                               "cosa", "quali", "quale", "perché", "perche", "come ", "quant", "dimmi", "scrivi", "riscriv", "migliora", "valuta", "cerca",
                                               "italian", "ingles", "english", "frances", "spagnol", "tedesc", "in breve", "punti",
                                               "summar", "translat", "explain", "correct", "analy", "list", "extract", "compare", "answer", "what", "which", "why",
                                               "how ", "tell", "write", "rewrite", "improve", "find", "short", "brief", "points" };

        public static bool WantsCopyOnly(string question)
        {
            string rest = Regex.Replace(WithoutUrls(question).ToLowerInvariant(), @"\s+", " ").Trim();
            if (Regex.Replace(rest, @"[\p{P}\s]", "").Length == 0) return true;
            return CopyWords.Any(w => StartsWord(rest, w)) && !TaskWords.Any(w => StartsWord(rest, w));
        }

        // La parola (o l'inizio di parola) w compare all'inizio di una parola: "scrivi" non vale in "trascrivi"
        static bool StartsWord(string text, string w)
        {
            return Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(w.Trim()) + (w.EndsWith(" ") ? @"(?![\p{L}\p{N}])" : ""));
        }

        // ---- Scaricamento ----

        public static WebPage Fetch(string url, CancellationToken cancel)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { } }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CorbolPad/" + AppVersion.Text;
            req.Accept = "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5";
            req.Headers[HttpRequestHeader.AcceptLanguage] = L.Code == "en" ? "en,it;q=0.5" : L.Code + ",en;q=0.8";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 10;
            req.Timeout = req.ReadWriteTimeout = TimeoutMs;
            using (cancel.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        string type = (resp.ContentType ?? "").ToLowerInvariant();
                        bool html = type.Contains("html") || type.Length == 0;
                        bool text = type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Contains("javascript");
                        if (!html && !text)
                            throw new Exception(L.F("non è una pagina di testo ({0})", "it is not a text page ({0})", type.Split(';')[0].Trim()));
                        byte[] data;
                        using (var st = resp.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            var buf = new byte[65536];
                            int n;
                            while ((n = st.Read(buf, 0, buf.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                if (ms.Length + n > MaxBytes) break;            // pagine enormi: basta l'inizio
                                ms.Write(buf, 0, n);
                            }
                            data = ms.ToArray();
                        }
                        string raw = Decode(data, Charset(type), html);
                        var page = new WebPage { Url = resp.ResponseUri != null ? resp.ResponseUri.AbsoluteUri : url };
                        if (html && !(type.Length == 0 && !LooksLikeHtml(raw)))
                        {
                            page.Title = HtmlTitle(raw);
                            page.Text = HtmlToText(raw);
                        }
                        else page.Text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                        if (string.IsNullOrEmpty(page.Title)) page.Title = resp.ResponseUri != null ? resp.ResponseUri.Host : new Uri(url).Host;
                        return page;
                    }
                }
                catch (WebException ex)
                {
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                    var r = ex.Response as HttpWebResponse;
                    if (r != null)
                    {
                        int code = (int)r.StatusCode;
                        if (code == 401 || code == 403)
                            throw new Exception(L.F("il sito non permette di leggere la pagina (errore {0})", "the site does not allow reading the page (error {0})", code));
                        if (code == 404) throw new Exception(L.T("la pagina non esiste (errore 404)", "the page does not exist (error 404)"));
                        throw new Exception(L.F("il sito ha risposto con l'errore {0}", "the site answered with error {0}", code));
                    }
                    if (ex.Status == WebExceptionStatus.Timeout) throw new Exception(L.T("il sito non risponde", "the site does not answer"));
                    if (ex.Status == WebExceptionStatus.NameResolutionFailure) throw new Exception(L.T("sito non trovato: controlla l'indirizzo e la connessione", "site not found: check the address and the connection"));
                    throw new Exception(ex.Message);
                }
            }
        }

        static string Charset(string contentType)
        {
            var m = Regex.Match(contentType ?? "", @"charset\s*=\s*""?([\w\-]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Decode(byte[] data, string charset, bool html)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            if (charset == null && html)
            {
                // <meta charset="..."> o <meta http-equiv="Content-Type" content="...; charset=...">
                string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 8192));
                var m = Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?\s*([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) charset = m.Groups[1].Value;
            }
            Encoding enc = null;
            if (charset != null) try { enc = Encoding.GetEncoding(charset.Trim()); } catch { }
            if (enc == null || enc is UTF8Encoding)
            {
                // Senza indicazioni: UTF-8 se è valido, altrimenti Windows-1252
                try { return new UTF8Encoding(false, true).GetString(data); }
                catch { if (enc != null) return Encoding.UTF8.GetString(data); enc = Encoding.GetEncoding(1252); }
            }
            return enc.GetString(data);
        }

        static bool LooksLikeHtml(string s)
        {
            string h = s.Length > 2000 ? s.Substring(0, 2000) : s;
            return Regex.IsMatch(h, @"<(!doctype|html|head|body|p|div)\b", RegexOptions.IgnoreCase);
        }

        // ---- Da HTML a testo ----

        const RegexOptions RI = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        public static string HtmlTitle(string html)
        {
            var m = Regex.Match(html, @"<meta[^>]+property\s*=\s*[""']og:title[""'][^>]*content\s*=\s*""([^""]*)""", RI);
            if (!m.Success) m = Regex.Match(html, @"<meta[^>]+property\s*=\s*[""']og:title[""'][^>]*content\s*=\s*'([^']*)'", RI);
            if (!m.Success) m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RI);
            if (!m.Success) m = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RI);
            if (!m.Success) return null;
            string t = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]*>", " "));
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 0 ? t : null;
        }

        // Un tag qualsiasi, anche con ">" dentro i valori degli attributi
        static readonly Regex TagRx = new Regex(@"<(/?)([a-zA-Z][\w:\-]*)((?:""[^""]*""|'[^']*'|[^'"">])*)>", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly HashSet<string> VoidTags = new HashSet<string> { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param" };

        public static string HtmlToText(string html)
        {
            string s = Regex.Replace(html, @"<!--.*?-->", " ", RI);
            s = Regex.Replace(s, @"<(script|style|template|noscript|svg|math)\b.*?</\1\s*>", " ", RI);
            var bm = Regex.Match(s, @"<body\b(?:""[^""]*""|'[^']*'|[^'"">])*>(.*)</body>", RI);
            string body = bm.Success ? bm.Groups[1].Value : Regex.Replace(s, @"<head\b.*?</head\s*>", " ", RI);

            // Prima si tolgono menu, piè di pagina, elementi nascosti, cookie e abbonamenti; se così sparisce quasi tutto
            // il testo (un sito che chiama "menu" il contenuto), si tolgono solo gli elementi sicuramente di contorno
            int all = PlainLength(body);
            string clean = Remove(body, (n, a) => IsChrome(n, a, true));
            if (all > 1000 && PlainLength(clean) < all * 3 / 10) clean = Remove(body, (n, a) => IsChrome(n, a, false));

            // Il contenuto principale: <main> o l'<article> più lungo, se contengono davvero il testo della pagina
            string best = null;
            int bestLen = 0;
            foreach (var r in Find(clean, (n, a) => n == "main" || n == "article" || Regex.IsMatch(a, @"\brole\s*=\s*[""']?main\b", RegexOptions.IgnoreCase)))
            {
                string inner = clean.Substring(r[2], r[3] - r[2]);
                int len = PlainLength(inner);
                if (len > bestLen) { best = inner; bestLen = len; }
            }
            if (best != null && bestLen >= Math.Max(400, PlainLength(clean) / 4)) clean = best;
            return BlocksToText(clean);
        }

        // Elementi di contorno. byName: anche quelli riconosciuti da id e class (cookie, menu...), meno sicuri
        static readonly string[] ChromeWords = { "cookie", "cookies", "consent", "gdpr", "cmp", "paywall", "accept", "navbar", "nav", "menu", "breadcrumb", "breadcrumbs",
                                                 "sidebar", "share", "sharing", "social", "dropdown", "editsection", "noprint", "newsletter", "popup", "modal",
                                                 "advert", "advertising", "ad", "ads", "adv", "banner", "skip", "skiplink", "toolbar", "subscribe", "login", "signin" };

        static bool IsChrome(string name, string attrs, bool byName)
        {
            switch (name)
            {
                case "nav": case "footer": case "aside": case "form": case "dialog": case "button": case "select": case "iframe":
                case "object": case "canvas": case "input": case "textarea": case "label":
                    return true;
            }
            if (attrs.Length == 0) return false;
            if (Regex.IsMatch(attrs, @"(?:^|\s)hidden(?:\s|=|/|$)", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(attrs, @"\baria-hidden\s*=\s*[""']?true", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(attrs, @"\bstyle\s*=\s*[""'][^""']*display\s*:\s*none", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(attrs, @"\brole\s*=\s*[""']?(?:navigation|banner|contentinfo|dialog|alertdialog|search|complementary)\b", RegexOptions.IgnoreCase)) return true;
            if (!byName) return false;
            foreach (Match m in Regex.Matches(attrs, @"\b(?:id|class)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase))
            {
                string v = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value;
                // "prompt-to-accept", "vector-dropdown", "cookieBanner": si guardano i pezzi del nome
                v = Regex.Replace(v, @"([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
                foreach (var part in Regex.Split(v, @"[^a-z0-9]+"))
                    if (part.Length > 0 && Array.IndexOf(ChromeWords, part) >= 0) return true;
            }
            return false;
        }

        // Gli elementi scelti (i più esterni), come [inizio, fine, inizio contenuto, fine contenuto]; gli elementi annidati
        // con lo stesso nome si contano, così la chiusura trovata è quella giusta
        static List<int[]> Find(string html, Func<string, string, bool> pick)
        {
            var res = new List<int[]>();
            var m = TagRx.Match(html);
            while (m.Success)
            {
                string name = m.Groups[2].Value.ToLowerInvariant(), attrs = m.Groups[3].Value;
                if (m.Groups[1].Length == 0 && !VoidTags.Contains(name) && !attrs.TrimEnd().EndsWith("/") && pick(name, attrs))
                {
                    int depth = 1;
                    var n = m.NextMatch();
                    while (n.Success)
                    {
                        if (string.Equals(n.Groups[2].Value, name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (n.Groups[1].Length > 0) depth--;
                            else if (!n.Groups[3].Value.TrimEnd().EndsWith("/")) depth++;
                            if (depth == 0) break;
                        }
                        n = n.NextMatch();
                    }
                    if (n.Success)
                    {
                        res.Add(new[] { m.Index, n.Index + n.Length, m.Index + m.Length, n.Index });
                        m = TagRx.Match(html, n.Index + n.Length);
                        continue;
                    }
                    // Senza chiusura (HTML sbagliato): solo il tag d'apertura
                    res.Add(new[] { m.Index, m.Index + m.Length, m.Index + m.Length, m.Index + m.Length });
                }
                m = m.NextMatch();
            }
            return res;
        }

        static string Remove(string html, Func<string, string, bool> pick)
        {
            var sb = new StringBuilder(html.Length);
            int pos = 0;
            foreach (var r in Find(html, pick))
            {
                sb.Append(html, pos, r[0] - pos).Append(' ');
                pos = r[1];
            }
            return sb.Append(html, pos, html.Length - pos).ToString();
        }

        static int PlainLength(string html)
        {
            return Regex.Replace(TagRx.Replace(html, " "), @"\s+", " ").Trim().Length;
        }

        static string BlocksToText(string s)
        {
            // <pre>: si tengono spazi e a capo così come sono
            var pres = new List<string>();
            s = Regex.Replace(s, @"<pre\b(?:""[^""]*""|'[^']*'|[^'"">])*>(.*?)</pre\s*>", m =>
            {
                string p = WebUtility.HtmlDecode(TagRx.Replace(Regex.Replace(m.Groups[1].Value, @"<br\s*/?>", "\n", RI), ""));
                pres.Add(p.Replace("\r\n", "\n").Trim('\n'));
                return "\n\n\u0002" + (pres.Count - 1) + "\u0002\n\n";
            }, RI);

            s = Regex.Replace(s, @"[\r\n\t]+", " ");
            s = TagRx.Replace(s, m =>
            {
                bool close = m.Groups[1].Length > 0;
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "br": return "\n";
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": return "\n\n";
                    case "li": return close ? "\n" : "\n- ";
                    case "td": case "th": return close ? "" : "\t";
                    case "p": case "div": case "section": case "article": case "main": case "header": case "blockquote": case "ul": case "ol": case "dl":
                    case "table": case "tr": case "thead": case "tbody": case "tfoot": case "figure": case "figcaption": case "address": case "details":
                    case "summary": case "hr": case "dd": case "dt": case "caption":
                        return "\n";
                    default: return "";
                }
            });
            s = WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');

            var sb = new StringBuilder();
            int blank = 0;
            bool dash = false, lastItem = false;
            foreach (var raw in s.Split('\n'))
            {
                string line = Regex.Replace(raw, @"[ \f\v\u200B]+", " ");
                line = Regex.Replace(line, @" ?\t ?", "\t").Trim(' ').Trim('\t');
                if (line.Length == 0) { blank++; continue; }
                if (line == "-") { dash = true; continue; }          // voce di elenco con il testo nelle righe dopo (<li><p>…)
                if (dash && !line.StartsWith("- ")) line = "- " + line;
                bool item = line.StartsWith("- ");
                // Voci consecutive dello stesso elenco: senza righe vuote in mezzo
                if (sb.Length > 0) sb.Append((blank > 0 && !(item && lastItem)) || IsPre(line) ? "\n\n" : "\n");
                blank = 0; dash = false; lastItem = item;
                if (IsPre(line)) { line = pres[int.Parse(line.Trim('\u0002'))]; lastItem = false; }
                sb.Append(line.Replace("\t", " | "));
            }
            return sb.ToString();
        }

        static bool IsPre(string line) { return line.Length > 2 && line[0] == '\u0002' && line[line.Length - 1] == '\u0002'; }

        // ---- Parti di un testo troppo lungo ----

        // Taglia a una riga vuota, altrimenti a un a capo, a fine frase o a uno spazio, mai prima di metà parte
        public static List<string> Split(string text, int max)
        {
            var res = new List<string>();
            int p = 0;
            while (text.Length - p > max)
            {
                int end = p + max, half = p + max / 2, cut = -1;
                foreach (var sep in new[] { "\n\n", "\n", ". ", " " })
                {
                    int i = text.LastIndexOf(sep, end - sep.Length, end - sep.Length - half + 1, StringComparison.Ordinal);
                    if (i >= half) { cut = i + sep.Length; break; }
                }
                if (cut < 0) cut = end;
                res.Add(text.Substring(p, cut - p));
                p = cut;
            }
            if (p < text.Length) res.Add(text.Substring(p));
            return res;
        }

        // Nome della scheda: il titolo della pagina, senza caratteri che non vanno nei nomi di file
        public static string TabName(string title)
        {
            string t = Regex.Replace(title ?? "", @"[\\/:*?""<>|\t\r\n]+", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim().TrimEnd('.');
            if (t.Length > 60) t = t.Substring(0, 60).TrimEnd() + "…";
            return t.Length > 0 ? t : null;
        }
    }
}
