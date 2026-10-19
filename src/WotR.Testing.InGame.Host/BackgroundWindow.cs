using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WotR.Testing.InGame.Host
{
    /// <summary>
    /// Keeps a game that a test run started minimized while it loads, so the window does not take the focus from the
    /// user's work. Minimizing hands the focus back to the window that had it. Stops once the game has loaded; the user
    /// can restore the window at any time after that.
    /// </summary>
    internal sealed class BackgroundWindow : IDisposable
    {
        private const int Minimize = 6;
        private readonly Func<bool> loaded;
        private readonly Timer timer;
        private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        private DateTime? loadedAt;

        internal BackgroundWindow(Func<bool> loaded)
        {
            this.loaded = loaded;
            timer = new Timer(_ => Check(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }

        private void Check()
        {
            try {
                if (loaded()) loadedAt = loadedAt ?? DateTime.UtcNow;
                // A few seconds past loading: the main menu may still activate the window once.
                if (DateTime.UtcNow > deadline || (loadedAt.HasValue && DateTime.UtcNow - loadedAt.Value > TimeSpan.FromSeconds(10))) {
                    Dispose();
                    return;
                }
                var window = GameWindow();
                if (window != IntPtr.Zero && !IsIconic(window)) ShowWindowAsync(window, Minimize);
            }
            catch (Exception) { Dispose(); }
        }

        private static IntPtr GameWindow()
        {
            var process = (uint)Process.GetCurrentProcess().Id;
            var found = IntPtr.Zero;
            EnumWindows((window, _) => {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != process || !IsWindowVisible(window)) return true;
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() != "UnityWndClass") return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public void Dispose() => timer.Dispose();

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    }
}
