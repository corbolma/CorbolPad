// Assistente: le azioni sul testo (menu Assistente e tasto destro) e le istruzioni mandate al modello.
// Le istruzioni sono in inglese (i modelli piccoli le seguono meglio); la lingua della risposta è indicata ogni volta.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace CorbolPad
{
    enum AiOutput
    {
        Replace,    // il risultato prende il posto della selezione (o di tutto il testo)
        NewTab,     // il risultato va in una scheda nuova: il testo di partenza non si tocca
        Insert      // "Chiedi" senza selezione: il risultato va nel punto del cursore
    }

    class AiAction
    {
        public string Id;
        public Func<string> Label;
        public Keys Keys;
        public string KeysText;
        public AiOutput Output;
        public bool NeedsText = true;       // senza selezione usa tutto il documento
        public Func<string> Task;           // cosa fare con il testo (in inglese)

        const string Common =
            "You are a writing assistant built into a plain-text editor. The user's text is between <text> and </text>. " +
            "Reply with the result only: no introduction, no explanations, no notes, no quotation marks around it, " +
            "no Markdown code fences, no <text> tags. Keep the original line breaks, lists and indentation unless the task requires otherwise.";

        // Lingua dell'interfaccia, per le risposte che l'utente deve leggere (riassunto, spiegazione)
        public static string UiLanguage()
        {
            switch (L.Code)
            {
                case "it": return "Italian";
                case "en": return "English";
                default:
                    try { return new CultureInfo(L.Code).EnglishName; } catch { return "English"; }
            }
        }

        static string Wrap(string text) { return "<text>\n" + text + "\n</text>"; }

        // La richiesta per il modello. question serve solo a "Chiedi".
        public AiRequest Build(string text, string question)
        {
            if (Id == "ask")
            {
                if (string.IsNullOrEmpty(text))
                    return AiRequest.Simple(
                        "You are an assistant built into a plain-text editor. Your reply is inserted into the document at the cursor position, " +
                        "so reply only with the text to insert, without introductions or comments. Use plain text, not Markdown, unless the request asks for it. " +
                        "Answer in the language of the request.",
                        question);
                return AiRequest.Simple(
                    Common + " Apply the user's request to the text; your reply replaces the text in the document. " +
                    "Unless the request says otherwise, keep the language of the text.",
                    question + "\n\n" + Wrap(text));
            }
            return AiRequest.Simple(Common + " Task: " + Task(), Wrap(text));
        }

        // ---- Testi lunghi, elaborati a parti ----

        // Una parte di un testo lungo da trasformare (correggere, tradurre...): ogni parte va per conto suo
        public AiRequest BuildPart(string text, int part, int parts)
        {
            if (parts <= 1) return Build(text, null);
            return AiRequest.Simple(Common + " Task: " + Task() +
                " The text is part " + part + " of " + parts + " of a longer document, processed one part at a time: " +
                "process only this part, even if it starts or ends in the middle of a topic, and do not add anything to connect it to the other parts.",
                Wrap(text));
        }

        // Appunti su una parte di un testo troppo lungo per essere letto tutto insieme (per riassunto e spiegazione)
        public static AiRequest BuildNotes(string text, int part, int parts)
        {
            return AiRequest.Simple(
                "You are helping to read a document that is too long to be read at once, so it is read one part at a time. " +
                "The user's text, between <text> and </text>, is part " + part + " of " + parts + ". " +
                "Write concise notes in " + UiLanguage() + " with the important content of this part: facts, names, numbers, dates, decisions, " +
                "and what any code, log or data does. One note per line, each line starting with \"- \". Reply with the notes only, no introduction.",
                Wrap(text));
        }

        // Pannello: appunti su una parte di una scheda troppo lunga, utili a rispondere alla domanda dell'utente
        public static AiRequest BuildQuestionNotes(string text, string question, int part, int parts)
        {
            return AiRequest.Simple(
                "You are helping to answer a question about a document that is too long to be read at once, so it is read one part at a time. " +
                "The user's message contains the question, then part " + part + " of " + parts + " of the document between <text> and </text>. " +
                "Write concise notes, in the language of the question, with everything in this part that is useful to answer the question. " +
                "If the question asks for an overview, an explanation or a summary, note the main content of this part " +
                "(for source code: what each class, function or section does). If nothing in this part is relevant, reply with \"- (nothing relevant)\". " +
                "One note per line, each line starting with \"- \". Reply with the notes only: do not answer the question yet.",
                "Question: " + question + "\n\n" + Wrap(text));
        }

        // Appunti troppo lunghi: si condensano
        public static AiRequest BuildMerge(string notes)
        {
            return AiRequest.Simple(
                "The user's text, between <text> and </text>, contains notes taken from consecutive parts of a long document. " +
                "Merge them into shorter notes in " + UiLanguage() + ": keep the important information and the order, drop repetitions. " +
                "One note per line, each line starting with \"- \". Reply with the notes only, no introduction.",
                Wrap(notes));
        }

        // Il risultato finale fatto dagli appunti su tutte le parti
        public AiRequest BuildFromNotes(string notes)
        {
            return AiRequest.Simple(Common +
                " The text between <text> and </text> is not the document itself but notes taken from all its consecutive parts, " +
                "because the document is too long to be read at once. Treat the notes as the content of the document. Task: " + Task(),
                Wrap(notes));
        }

        public static readonly List<AiAction> All = new List<AiAction>
        {
            new AiAction
            {
                Id = "fix", Label = () => L.T("&Correggi ortografia e grammatica", "&Fix spelling and grammar"),
                Keys = Keys.F7, KeysText = "F7", Output = AiOutput.Replace,
                Task = () => "Fix spelling, grammar and punctuation errors. Change nothing else: keep the wording, style, tone and meaning. " +
                             "Keep the same language as the text. If there are no errors, return the text unchanged."
            },
            new AiAction
            {
                Id = "rewrite", Label = () => L.T("&Riformula in modo più chiaro", "&Rewrite more clearly"), Output = AiOutput.Replace,
                Task = () => "Rewrite the text so that it is clearer and reads more fluently. Keep the meaning, the tone and the language of the text."
            },
            new AiAction
            {
                Id = "formal", Label = () => L.T("Più &formale", "More f&ormal"), Output = AiOutput.Replace,
                Task = () => "Rewrite the text in a more formal, professional tone. Keep the meaning and the language of the text."
            },
            new AiAction
            {
                Id = "shorter", Label = () => L.T("Più &breve", "&Shorter"), Output = AiOutput.Replace,
                Task = () => "Make the text more concise: remove repetition and filler, keep all the important information. Keep the language of the text."
            },
            null,
            new AiAction
            {
                Id = "to-it", Label = () => L.T("Traduci in i&taliano", "Translate to &Italian"), Output = AiOutput.Replace,
                Task = () => "Translate the text into Italian. Keep names, code, numbers and formatting as they are."
            },
            new AiAction
            {
                Id = "to-en", Label = () => L.T("Traduci in i&nglese", "Translate to &English"), Output = AiOutput.Replace,
                Task = () => "Translate the text into English. Keep names, code, numbers and formatting as they are."
            },
            null,
            new AiAction
            {
                Id = "summary", Label = () => L.T("Ri&assumi (in una nuova scheda)", "Su&mmarize (in a new tab)"), Output = AiOutput.NewTab,
                Task = () => "Summarize the text in " + UiLanguage() + ": a few sentences, or a short list with lines starting with \"- \" if the text covers several points. Plain text, no Markdown headings or bold."
            },
            new AiAction
            {
                Id = "explain", Label = () => L.T("&Spiega (in una nuova scheda)", "E&xplain (in a new tab)"), Output = AiOutput.NewTab,
                Task = () => "Explain in " + UiLanguage() + " what the text says or does. It may be prose, source code, a log, configuration or data: " +
                             "say what it is, then explain the important parts concisely. Plain text, no Markdown headings or bold."
            },
            null,
            new AiAction
            {
                Id = "ask", Label = () => L.T("C&hiedi all'assistente...", "&Ask the assistant..."),
                Keys = Keys.Control | Keys.K, KeysText = "F1, Ctrl+K", Output = AiOutput.Replace, NeedsText = false
            }
        };
    }
}
