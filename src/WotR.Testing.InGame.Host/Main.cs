using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityModManagerNet;

namespace WotR.Testing.InGame.Host
{
    /// <summary>UnityModManager entry point. Never runs tests by itself; it only waits for an adapter connection.</summary>
    public static class Main
    {
        public static bool Load(UnityModManager.ModEntry entry)
        {
            // Set before any test code loads: the offline library's resolver stays off in the player.
            AppDomain.CurrentDomain.SetData("WotR.Testing.Mode", "ingame");
            var directory = Path.GetFullPath(entry.Path);
            // This mod's own libraries live beside it; the game folder does not contain them.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) && IsOwn(path) ? Assembly.LoadFrom(path) : null;
            };
            try {
                return Start(entry);
            }
            catch (Exception error) {
                entry.Logger.Error("WotR.Testing host failed to start: " + error);
                return false;
            }
        }

        private static bool IsOwn(string path)
            => Path.GetFileName(path).StartsWith("WotR.Testing.", StringComparison.Ordinal);

        // Separate method: its types resolve only after the handler above is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Start(UnityModManager.ModEntry entry) => HostService.Start(entry);
    }
}
