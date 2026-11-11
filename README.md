<h1 align="center">AppPad</h1>

<p align="center">
  Blocco note a schede, veloce anche con file di centinaia di MB, che riapre le schede (anche quelle non salvate) dove le avevi lasciate.<br>
  Leggero, senza installare nulla di aggiuntivo, senza permessi di amministratore.<br>
  <a href="#english">English below</a>
</p>

---

## Italiano

### Cosa fa
- **Schede** come in Notepad++: nuova con `Ctrl+N`, doppio clic sullo spazio vuoto o il pulsante **+**. Si chiudono con la **×**, il tasto centrale del mouse o `Ctrl+W`, e si riordinano trascinandole. Si passa dall'una all'altra con `Ctrl+Tab` / `Ctrl+Maiusc+Tab`. `Ctrl+Maiusc+T` riapre l'ultima scheda chiusa. Quando le schede sono tante compare la freccia ▾ con l'elenco completo. Il tasto destro su una scheda offre: chiudi le altre, chiudi quelle a destra, copia percorso, apri la cartella.
- **File grandi**: il file viene letto in background con una barra di avanzamento e il tasto *Annulla*, e la finestra resta utilizzabile. Un file da 300 MB (3,3 milioni di righe) si apre in circa un secondo e occupa circa 330 MB di memoria.
- **Sessione ripristinata**: alla chiusura non chiede niente, come il Blocco note di Windows 11. Alla riapertura ritrovi tutte le schede, con il cursore dov'era e anche il testo **non salvato** (schede senza titolo e file modificati). Le schede si caricano solo quando le apri, così l'avvio resta immediato. Ogni 30 secondi AppPad salva anche una copia di sicurezza, nel caso il PC si spenga all'improvviso; i documenti oltre 64 MB vengono salvati solo alla chiusura.
- **Codifiche**: riconosce UTF-8 (con e senza BOM), UTF-16 LE/BE e ANSI. Dal menu *Codifica* o dalla barra di stato puoi scegliere con quale codifica salvare, oppure riaprire il file con un'altra codifica. Se un carattere non esiste nella codifica scelta (per esempio un emoji in ANSI) te lo dice prima di salvare.
- **Fine riga** Windows (CRLF), Unix (LF) e Mac (CR): riconosciuti all'apertura, si possono convertire.
- **Trova e sostituisci** (`Ctrl+F`, `Ctrl+H`) in una barra in basso: ricerca mentre scrivi, `F3` / `Maiusc+F3`, maiuscole/minuscole, parola intera, espressioni regolari (con `\1`... nella sostituzione), *Sostituisci tutto* (annullabile in un colpo solo con `Ctrl+Z`). `Ctrl+G` porta a una riga.
- **Per chi scrive codice**: numeri di riga, multi-cursore (`Ctrl` + clic), selezione rettangolare (`Alt` + trascina), duplica riga (`Ctrl+D`), elimina riga (`Ctrl+Maiusc+L`), maiuscole/minuscole (`Ctrl+Maiusc+U` / `Ctrl+U`), spazi e fine riga visibili.
- **Avviso** quando un file aperto viene modificato da un altro programma, con la proposta di ricaricarlo.
- Tema **chiaro / scuro / come Windows**, carattere a scelta, zoom (`Ctrl` + rotellina, `Ctrl++`, `Ctrl+-`, `Ctrl+0`), a capo automatico (`Alt+Z`), file recenti, trascinamento dei file sulla finestra, data e ora con `F5`.
- **Una sola finestra**: aprendo altri file (doppio clic, *Apri con*), questi si aggiungono come schede alla finestra già aperta.

### Assistente (facoltativo)
È **spento** finché non lo attivi in *Assistente > Impostazioni dell'assistente*. Puoi scegliere tra:
- **Assistente locale**: gira sul PC e il testo non esce dal computer. AppPad scarica su richiesta il modello [Qwen3.5](https://huggingface.co/Qwen) (4B, circa 2,6 GB, o 2B, circa 1,2 GB, per i PC con meno di 16 GB di RAM) e [llama.cpp](https://github.com/ggml-org/llama.cpp), e controlla l'impronta SHA-256 di ogni file. Puoi anche usare un tuo modello `.gguf`. Gira sul processore o sulla scheda grafica: *Misura la velocità* prova entrambi e sceglie la GPU solo se è almeno del 30% più veloce. Si chiude da solo dopo 5 minuti di inattività.
- **Claude** (Anthropic), con la tua chiave API.
- **Claude Code**, con il tuo abbonamento Claude (solo uso personale), se è installato sul PC e hai fatto il login: niente chiave API. AppPad avvia `claude -p` senza strumenti (non può leggere file né eseguire comandi), senza server MCP e senza salvare la sessione, e gli passa solo il testo su cui lavora l'assistente. Modello: predefinito di Claude Code, opus, sonnet o haiku.
- **Un servizio compatibile OpenAI**: OpenAI, Qwen (Alibaba Cloud), OpenRouter, oppure Ollama e LM Studio sul PC.

Le chiavi API restano sul PC, cifrate con la protezione dati di Windows. *Prova connessione* verifica che tutto funzioni.

Cosa fa:
- **Chiedi all'assistente** con `F1` (o `Ctrl+K`, o il pulsante a destra della barra dei menu): un pannello di conversazione **a lato o sotto** l'editor. L'assistente **legge le schede aperte**: di base la scheda attiva (o il testo selezionato), più le schede che nomini nella domanda ("confronta con note.txt"); oppure, a scelta, tutte le schede aperte. Non serve incollare nulla. Se la scheda è troppo lunga, l'assistente cerca nel file i passaggi che contengono le parole della domanda (frasi "tra virgolette" cercate così come sono) e li legge insieme alla parte intorno al cursore. La risposta si inserisce al cursore, al posto della selezione o in una nuova scheda.
- Dal menu *Assistente* e dal tasto destro: **correggi** (`F7`), riformula, più formale, più breve, traduci in italiano o in inglese. Il risultato compare mentre viene scritto al posto della selezione (o di tutto il documento), e si annulla con un solo `Ctrl+Z`. *Riassumi* e *Spiega* scrivono in una nuova scheda. `Esc` ferma e rimette il testo com'era. **Testi lunghi**: l'assistente li divide da solo in parti (a fine paragrafo) e le elabora una dopo l'altra; *Riassumi* e *Spiega* prendono appunti su ogni parte e scrivono il risultato dagli appunti. Con `Esc` le parti già finite restano. Limite: 2 milioni di caratteri.
- La prima volta che usi un servizio in rete, AppPad chiede conferma prima di inviare il testo.

### Installare e aggiornare
- **Installazione**: scarica `AppPad-Setup-X.Y.Z.exe` dalle [Releases](https://github.com/corbolma/AppPad/releases/latest) e avvialo. Installa solo per il tuo utente, senza permessi di amministratore, in `%LOCALAPPDATA%\Programs\AppPad`. A scelta aggiunge "Apri con AppPad" al tasto destro dei file e propone AppPad in "Apri con" e nelle App predefinite per i file di testo. `/silent` installa senza finestre.
- **Aggiornamento automatico** (attivo di default; si spegne dal menu *? > Aggiorna automaticamente*):
  - una volta al giorno AppPad controlla su GitHub se c'è una versione nuova;
  - se c'è, scarica l'installer e lo verifica con l'impronta SHA-256 pubblicata nella release;
  - sulla barra dei menu compare **Aggiornamento pronto**, per installarlo subito; altrimenti si installa da solo, in silenzio, **quando chiudi AppPad**;
  - alla riapertura ritrovi le stesse schede, anche quelle non salvate.

  *? > Controlla aggiornamenti* controlla subito. Una copia di AppPad non installata (avviata da un'altra cartella) avvisa e basta.
- **Disinstallazione**: Impostazioni → App → App installate → AppPad. Chiede se eliminare anche i dati (impostazioni, schede non salvate, file dell'assistente); di default li tiene.

### Dove salva i dati
- Impostazioni: `%APPDATA%\AppPad\settings.txt`
- Sessione: `%LOCALAPPDATA%\AppPad\session.txt`, e il testo non salvato in `%LOCALAPPDATA%\AppPad\backup\`
- Motore dell'editor: `%LOCALAPPDATA%\AppPad\bin\<impronta>\Scintilla.dll` (estratto dall'exe al primo avvio)
- Assistente locale: `%LOCALAPPDATA%\AppPad\ai\` (modello e llama.cpp; si cancella con *Cancella i file* nelle impostazioni dell'assistente)

Il salvataggio scrive prima un file temporaneo nella stessa cartella e poi sostituisce l'originale: se qualcosa va storto a metà (disco pieno, chiavetta rimossa), il file originale resta intatto.

### Compilare dal sorgente
Non serve installare nulla: si usa il compilatore C# incluso in Windows (.NET Framework 4.x, 64 bit).

```bat
build.bat                    :: crea AppPad.exe (con Scintilla.dll e l'icona incorporate)
build-installer.bat          :: crea dist\AppPad-Setup-X.Y.Z.exe con la versione di Version.cs
build-installer.bat patch    :: prima aumenta la versione: 1.0.0 -> 1.0.1 (anche: minor, major, oppure X.Y.Z)
```

Per pubblicare una versione: crea su GitHub una release con tag `vX.Y.Z`, allega `dist\AppPad-Setup-X.Y.Z.exe` e scrivi nelle note la riga `SHA-256: ...` che `build-installer.bat` stampa alla fine (la trovi anche in `dist\release-notes-X.Y.Z.md`). Senza quella riga l'aggiornamento automatico non scarica l'installer e si limita ad aprire la pagina della release.

Il motore dell'editor è [Scintilla](https://www.scintilla.org) 5.6.9 (lo stesso di Notepad++), in `scintilla\` con la sua licenza: vedi [scintilla/README.md](scintilla/README.md).

Traduzioni in altre lingue: vedi [lang/README.md](lang/README.md).

### Componenti di terze parti
AppPad è distribuito con licenza MIT. Usa:
- [Scintilla](https://www.scintilla.org) di Neil Hodgson, incorporato nell'exe: [licenza](https://www.scintilla.org/License.txt) in stile MIT/ISC.
- [llama.cpp](https://github.com/ggml-org/llama.cpp) (licenza MIT), solo se scegli l'assistente locale: viene scaricato dalla release ufficiale `b12110` su GitHub, non è incluso in AppPad.
- [Qwen3.5](https://huggingface.co/Qwen) di Alibaba (licenza Apache 2.0), solo se scegli l'assistente locale: il modello viene scaricato da Hugging Face nella versione quantizzata da [Unsloth](https://huggingface.co/unsloth), non è incluso in AppPad.

### Privacy
AppPad non raccoglie dati. Si collega a Internet solo per:
- **aggiornamenti**: una volta al giorno legge da `api.github.com` qual è l'ultima versione, e scarica l'installer da GitHub se ce n'è una nuova. Non invia nulla di tuo. Si spegne dal menu *? > Aggiorna automaticamente*;
- **assistente locale**, se lo attivi: solo per scaricare il modello e llama.cpp; poi il testo resta sul PC;
- **Claude o un altro servizio**: il testo su cui lavora l'assistente e le domande che scrivi vengono inviati a quel servizio, e valgono le sue condizioni.

---

## English

**AppPad** is a tabbed notepad for Windows that stays fast with files of several hundred MB and restores your tabs, including unsaved text, when you open it again. No administrator rights required.

- **Tabs**: new (`Ctrl+N`, **+**, double-click the empty area), close (**×**, middle click, `Ctrl+W`), drag to reorder, `Ctrl+Tab`, reopen closed tab (`Ctrl+Shift+T`), tab list when they don't fit, and a context menu.
- **Large files** are loaded in the background with progress and *Cancel*: a 300 MB file (3.3 million lines) opens in about a second and uses about 330 MB of memory.
- **Session restore**: closing never asks anything. Tabs, caret positions and unsaved text come back next time, and tabs are loaded only when you open them. A safety backup is also written every 30 seconds (documents over 64 MB only on exit).
- **Encodings** UTF-8 (with or without BOM), UTF-16 LE/BE and ANSI: detected on open, can be changed on save or used to reopen the file. **Line endings** CRLF/LF/CR: detected and convertible.
- **Find/replace** bar with incremental search, match case, whole word, regular expressions and undoable *Replace all*. **Go to line** with `Ctrl+G`.
- **For code**: line numbers, multiple carets, rectangular selection, duplicate/delete line, change case, visible whitespace.
- Warning when a file is changed by another program, light/dark/system theme, font, zoom, word wrap, recent files, drag and drop, single window.
- **Optional assistant**, off until you turn it on in *Assistant > Assistant settings*:
  - **Local** (the text never leaves the PC): AppPad downloads [Qwen3.5](https://huggingface.co/Qwen) 4B or 2B and [llama.cpp](https://github.com/ggml-org/llama.cpp) on request and checks their SHA-256. You can also use your own `.gguf`. It runs on the CPU or the GPU, and *Measure speed* picks the GPU only if it is at least 30% faster.
  - **Claude** (Anthropic) with your API key.
  - **Claude Code** with your own Claude subscription (personal use only), if it is installed and you are logged in: no API key. AppPad runs `claude -p` without tools, without MCP servers and without saving the session, and sends only the text the assistant works on.
  - **Any OpenAI-compatible service**: OpenAI, Qwen (Alibaba Cloud), OpenRouter, Ollama, LM Studio.

  API keys are stored encrypted with Windows data protection.
  - `F1` (or `Ctrl+K`, or the button at the right of the menu bar) opens a **chat panel at the side or bottom**. The assistant **reads your open tabs**: the active tab (or the selection) plus any tab you name in the question, or optionally all open tabs. No pasting needed. Insert the answer at the cursor, in place of the selection or in a new tab.
  - The *Assistant* menu and the context menu offer fix (`F7`), rewrite, more formal, shorter, translate, summarize and explain. Results stream in place, one `Ctrl+Z` undoes them, and `Esc` stops and restores the text. Long texts are split automatically into parts (at paragraph ends) and processed one after the other; summarize and explain take notes on each part first. Up to 2 million characters.
- **Install**: run `AppPad-Setup-X.Y.Z.exe` from the [Releases](https://github.com/corbolma/AppPad/releases/latest).
  - It installs per user, without admin rights, and can add "Open with AppPad" to the right-click menu and offer AppPad for text files. `/silent` installs without windows.
  - **Automatic updates** (on by default, *? > Update automatically*): once a day AppPad checks GitHub, downloads the new installer and verifies its SHA-256. You can install it with one click, or it installs silently when you close AppPad; your tabs come back as they were.
- Build with `build.bat` (uses the C# compiler included in Windows); `build-installer.bat [patch|minor|major|X.Y.Z]` builds the installer and prints the `SHA-256:` line to paste into the GitHub release notes. Editor engine: [Scintilla](https://www.scintilla.org) 5.6.9, see [scintilla/README.md](scintilla/README.md). Translations: [lang/README.md](lang/README.md).
- **Third-party components**: AppPad is MIT licensed.
  - [Scintilla](https://www.scintilla.org/License.txt) is embedded in the exe.
  - [llama.cpp](https://github.com/ggml-org/llama.cpp) (MIT) and [Qwen3.5](https://huggingface.co/Qwen) (Apache 2.0, GGUF by [Unsloth](https://huggingface.co/unsloth)) are not bundled: they are downloaded only if you choose the local assistant.
- **Privacy**: no data collection. AppPad goes online only in two cases.
  - The daily update check (`api.github.com`) sends nothing about you and can be turned off.
  - If you turn on the assistant: the local assistant only downloads its files.
  - With Claude or another service, the text and your questions are sent to that service.
