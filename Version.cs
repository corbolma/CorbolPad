// Versione condivisa da AppPad.exe e dall'installer. Cambiala qui prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("AppPad")]
[assembly: AssemblyCopyright(AppPad.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]
// Senza questo attributo (csc non lo aggiunge) .NET usa la vecchia gestione dei percorsi, limitata a 260 caratteri
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.6.2", FrameworkDisplayName = ".NET Framework 4.6.2")]

namespace AppPad
{
    static class AppVersion
    {
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/AppPad";
        public const string Text = "1.0.4";
    }
}
