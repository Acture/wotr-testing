using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WotR.Testing.InGame.Host
{
    /// <summary>
    /// Keeps a game that a test run started from taking the focus while it loads: whenever the game becomes the
    /// foreground window, the focus goes back to the window the user had, as an Alt+Tab would. The game is never
    /// minimized by code: minimizing a fullscreen player during startup can stall it. Stops once the game has loaded.
    /// </summary>
    internal sealed class BackgroundWindow : IDisposable
    {
        private readonly IntPtr returnTo;
        private readonly Func<bool> loaded;
        private readonly Timer timer;
        private readonly uint process = (uint)Process.GetCurrentProcess().Id;
        private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        private DateTime? loadedAt;

        internal BackgroundWindow(long returnTo, Func<bool> loaded)
        {
            this.returnTo = new IntPtr(returnTo);
            this.loaded = loaded;
            timer = new Timer(_ => Check(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        }

        private void Check()
        {
            try {
                if (loaded()) loadedAt = loadedAt ?? DateTime.UtcNow;
                // A few seconds past loading: the main menu may still activate the window once.
                if (DateTime.UtcNow > deadline || (loadedAt.HasValue && DateTime.UtcNow - loadedAt.Value > TimeSpan.FromSeconds(15)) || !IsWindow(returnTo)) {
                    Dispose();
                    return;
                }
                var foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out var owner);
                // The foreground process may hand the focus to another window.
                if (owner == process) SetForegroundWindow(returnTo);
            }
            catch (Exception) { Dispose(); }
        }

        public void Dispose() => timer.Dispose();

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    }
}
