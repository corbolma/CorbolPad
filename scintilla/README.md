# Scintilla

`Scintilla.dll` (64 bit) è il motore dell'editor, lo stesso di Notepad++. Viene incorporata in `CorbolPad.exe` da `build.bat`.

- Versione: **5.6.9**, presa dallo zip ufficiale di SciTE per Windows: <https://www.scintilla.org/wscite569.zip>
- Licenza: `License.txt` (permissiva, di Neil Hodgson)

Per aggiornarla: scarica il nuovo `wsciteNNN.zip` (64 bit) da <https://www.scintilla.org/SciTEDownload.html>,
copia qui `Scintilla.dll` e `License.txt`, controlla in `Scintilla.cs` che i messaggi usati non siano cambiati e ricompila.
