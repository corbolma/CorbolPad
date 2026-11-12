@echo off
rem Compila CorbolPad.exe usando il compilatore C# incluso in Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore csc.exe a 64 bit non trovato.
  exit /b 1
)
cd /d "%~dp0"
if not exist corbolpad.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
if not exist scintilla\Scintilla.dll (
  echo Manca scintilla\Scintilla.dll ^(x64^): vedi scintilla\README.md
  exit /b 1
)
rem Traduzioni: ogni lang\*.txt (tranne _template.txt) viene incorporato nell'exe
set LANGRES=
for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",CorbolPad.lang.%%~nF.txt
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /unsafe /optimize+ /out:CorbolPad.exe ^
  /win32icon:corbolpad.ico /resource:corbolpad.ico,CorbolPad.corbolpad.ico /resource:scintilla\Scintilla.dll,CorbolPad.Scintilla.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  CorbolPad.cs MainForm.cs TabStrip.cs Document.cs Scintilla.cs Version.cs Lang.cs OldApp.cs AiDownloader.cs AiProviders.cs ClaudeCode.cs LocalAi.cs AiActions.cs AiSettingsForm.cs Updater.cs MainForm.Ai.cs MainForm.AiPanel.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo Creato CorbolPad.exe
