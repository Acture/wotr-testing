using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace WotR.Testing.Build
{
    /// <summary>
    /// Installs the in-game test host as its own UnityModManager mod. Writes only Mods/WotR.Testing.Host and refuses while
    /// a game from this installation runs, rather than replacing files the player has loaded. Never touches other mods.
    /// </summary>
    public sealed class InstallInGameHost : Task
    {
        public const string ModFolder = "WotR.Testing.Host";

        /// <summary>The complete host mod; nothing else from the payload folder is installed.</summary>
        public static readonly string[] Files = {
            "Info.json", "WotR.Testing.InGame.Host.dll", "WotR.Testing.InGame.dll", "WotR.Testing.InGame.Protocol.dll", "WotR.Testing.Runner.dll",
            "WotR.Testing.Runner.Xunit.bridge", "WotR.Testing.Runner.Fuzzing.bridge"
        };

        [Required] public string Payload { get; set; }
        [Required] public string GameRoot { get; set; }
        [Output] public string Installed { get; set; }

        public override bool Execute()
        {
            try {
                var root = Path.GetFullPath(GameRoot);
                var executable = Path.Combine(root, "Wrath.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("[ENV_MISSING] No WotR installation at " + root + "; set WrathInstallDir.");
                if (!Directory.Exists(Path.Combine(root, "Mods")))
                    throw new DirectoryNotFoundException("[ENV_MISSING] " + root + " has no Mods folder; install UnityModManager first.");
                var payload = Path.GetFullPath(Payload);
                var files = Files.Select(name => Path.Combine(payload, name)).ToArray();
                var missing = files.FirstOrDefault(file => !File.Exists(file));
                if (missing != null) throw new FileNotFoundException("The host payload is incomplete.", missing);
                foreach (var process in Process.GetProcessesByName("Wrath")) {
                    using (process) {
                        string path = null;
                        try { path = process.MainModule?.FileName; }
                        catch (Exception) { /* No access: assume it may be this installation. */ }
                        if (path == null || string.Equals(Path.GetFullPath(path), executable, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The game runs (process " + process.Id + "). Close it before installing the host; a loaded host is never replaced in place.");
                    }
                }
                Installed = Path.Combine(root, "Mods", ModFolder);
                Directory.CreateDirectory(Installed);
                // Earlier host files and UnityModManager's cached copies of them; nothing else lives in this folder.
                foreach (var stale in Directory.GetFiles(Installed).Where(file => Path.GetFileName(file).StartsWith("WotR.Testing.", StringComparison.Ordinal) ||
                             Path.GetFileName(file) == "Info.json"))
                    File.Delete(stale);
                foreach (var file in files) File.Copy(file, Path.Combine(Installed, Path.GetFileName(file)), true);
                Log.LogMessage(MessageImportance.High, "Installed the WotR.Testing in-game host in " + Installed +
                    ". Enable it in UnityModManager if needed and restart the game to load it.");
                return true;
            }
            catch (Exception error) { Log.LogErrorFromException(error, false); return false; }
        }
    }
}
