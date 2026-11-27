using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WotR.Testing.Offline;

namespace WotR.Testing.Snapshot
{
    /// <summary>
    /// wotr-snapshot navigation-settings --bundles DIR --game-version VERSION --output FILE: reads the AstarPath settings
    /// of every mechanics scene bundle in DIR into FILE (SceneNavigationSettings.FileName in a snapshot). New-WotrSnapshot.ps1
    /// runs it so that a snapshot needs no scene bundles.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] != "navigation-settings")
                    throw new ArgumentException("Usage: wotr-snapshot navigation-settings --bundles DIR --game-version VERSION --output FILE");
                var options = Options(args.Skip(1).ToArray());
                NavigationSettings(options["--bundles"], options["--game-version"], options["--output"]);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("wotr-snapshot: " + error.Message);
                return 1;
            }
        }

        private static Dictionary<string, string> Options(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Expected an option and its value at '{args[i]}'.");
                options[args[i]] = args[i + 1];
            }
            foreach (var required in new[] { "--bundles", "--game-version", "--output" })
                if (!options.ContainsKey(required)) throw new ArgumentException($"Missing {required}.");
            return options;
        }

        private static void NavigationSettings(string bundles, string gameVersion, string output)
        {
            var timer = Stopwatch.StartNew();
            var paths = Directory.GetFiles(bundles, SceneNavigationSettings.ScenePattern).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (paths.Count == 0) throw new InvalidOperationException($"{bundles} has no {SceneNavigationSettings.ScenePattern} bundles.");
            var scenes = new ConcurrentBag<SceneNavigationSettings.Scene>();
            var failures = new ConcurrentBag<string>();
            Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
            {
                try
                {
                    var found = SceneNavigationSettings.Read(path);
                    using var stream = File.OpenRead(path);
                    var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    scenes.Add(new SceneNavigationSettings.Scene(Path.GetFileName(path).ToLowerInvariant(), sha256, found));
                }
                catch (Exception error)
                {
                    failures.Add($"{Path.GetFileName(path)}: {error.Message}");
                }
            });
            // A scene left out would make battles in its area fall back to A*'s class defaults; fail instead.
            if (!failures.IsEmpty)
                throw new InvalidOperationException($"{failures.Count} scene bundle(s) could not be read:{Environment.NewLine}" + string.Join(Environment.NewLine, failures.OrderBy(f => f, StringComparer.Ordinal)));
            SceneNavigationSettings.Write(output, gameVersion, scenes);

            // The file must give back exactly the fields the bundles hold, as the offline library reads it.
            var reloaded = SceneNavigationSettings.Load(output);
            foreach (var scene in scenes)
            {
                var read = reloaded.Scenes[scene.Bundle].AstarPaths;
                var difference = read.Count != scene.AstarPaths.Count ? $"{read.Count} AstarPath components instead of {scene.AstarPaths.Count}"
                    : scene.AstarPaths.Zip(read, (expected, actual) => SceneNavigationSettings.Difference(expected, actual)).FirstOrDefault(d => d != null);
                if (difference != null) throw new InvalidOperationException($"{output} does not give back {scene.Bundle}: {difference}");
            }
            Console.WriteLine($"Read {paths.Count} mechanics scene bundles, {scenes.Count(s => s.AstarPaths.Count > 0)} with an AstarPath, in {timer.Elapsed.TotalSeconds:F1} s: {output}");
        }
    }
}
