using System;
using System.Threading;
using UnityEngine;
using UnityModManagerNet;
using WotR.Testing.InGame.Protocol;
using WotR.Testing.Runner;

namespace WotR.Testing.InGame.Host
{
    internal static class HostService
    {
        private const int ReadinessInterval = 30;
        private static MainThreadDispatcher dispatcher;
        private static HostEnvironment environment;
        private static RunnerServer server;
        private static HostAdvertisement advertisement;
        private static BackgroundWindow background;

        internal static bool Start(UnityModManager.ModEntry entry)
        {
            dispatcher = new MainThreadDispatcher(TimeSpan.FromMilliseconds(5));
            dispatcher.UnhandledException += error => {
                environment?.MarkDirty("unobserved exception on the main thread: " + error.Message);
                entry.Logger.Error("Unobserved exception from test code on the main thread: " + error);
            };
            environment = new HostEnvironment(dispatcher, entry);
            // A game a test run started stays minimized while it loads and keeps updating when unfocused.
            if (LaunchRequest.ReadRecent()?.Minimize == true) {
                Application.runInBackground = true;
                background = new BackgroundWindow(() => environment.Loaded);
            }
            InGameRuntime.Attach(dispatcher, environment.MarkDirty);
            var frames = 0;
            // Update runs on Unity's main thread once per frame while this mod is active.
            entry.OnUpdate = (_, __) => {
                dispatcher.Update(Time.frameCount);
                if (frames++ % ReadinessInterval == 0) environment.RefreshReadiness();
            };
            advertisement = HostAdvertisement.ForCurrentProcess(Welcome.InGame, RunnerEnvironment.HostVersion, environment.InstallRoot, environment.GameVersion);
            server = new RunnerServer(environment, advertisement);
            server.Log += message => entry.Logger.Log(message);
            server.Start();
            advertisement.Write();
            entry.OnUnload = _ => { Stop(); return true; };
            Application.quitting += Stop;
            entry.Logger.Log("WotR.Testing host " + RunnerEnvironment.HostVersion + " is waiting for test sessions (process " + advertisement.ProcessId + ").");
            return true;
        }

        private static void Stop()
        {
            var running = Interlocked.Exchange(ref server, null);
            if (running == null) return;
            advertisement.Remove();
            background?.Dispose();
            running.Dispose();
            dispatcher.Stop();
        }
    }
}
