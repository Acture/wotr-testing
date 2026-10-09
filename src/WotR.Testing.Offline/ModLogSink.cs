using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Captures every UnityModManager log line (the manager's and each mod's ModLogger) as it is written. UnityModManager
    /// keeps only its last few hundred lines in memory and flushes to Log.txt in the installation from the player's
    /// update loop; neither suits a test report, and the installation is never written.
    /// </summary>
    public static class ModLogSink
    {
        /// <summary>Full logs larger than this are written gzip-compressed.</summary>
        public const long CompressAboveBytes = 5 * 1024 * 1024;

        private const int Excerpt = 20;
        private static readonly ConcurrentQueue<string> Lines = new();
        private static readonly Regex Source = new(@"^\[([^\]]+)\]");

        internal static void Add(string line)
        {
            if (line != null) Lines.Enqueue(line);
        }

        /// <summary>Lines written with the given UnityModManager prefix, for example a mod Id or "Manager".</summary>
        public static List<string> For(string source)
            => Lines.Where(line => SourceOf(line) == source).ToList();

        private static string SourceOf(string line)
        {
            var match = Source.Match(line);
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>
        /// Writes one file per source into <paramref name="directory"/> and returns a compact summary for environment.json:
        /// line counts, error lines, and the first and last lines with consecutive repeats collapsed.
        /// </summary>
        internal static object Write(string directory)
        {
            var bySource = Lines.ToArray().GroupBy(SourceOf).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (bySource.Count == 0) return Array.Empty<object>();
            Directory.CreateDirectory(directory);
            return bySource.Select(group =>
            {
                var lines = group.ToList();
                var name = string.Concat((group.Key.Length == 0 ? "unprefixed" : group.Key).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var text = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine);
                var file = Path.Combine(directory, name + ".log");
                if (text.Length > CompressAboveBytes)
                {
                    file += ".gz";
                    using var output = File.Create(file);
                    using var gzip = new GZipStream(output, CompressionLevel.Optimal);
                    gzip.Write(text, 0, text.Length);
                }
                else
                {
                    File.WriteAllBytes(file, text);
                }
                var collapsed = Collapse(lines);
                return (object)new
                {
                    source = group.Key,
                    lines = lines.Count,
                    file,
                    errors = lines.Where(l => l.Contains("[Error]") || l.Contains("[Exception]")).Take(100).ToArray(),
                    first = collapsed.Take(Excerpt).ToArray(),
                    last = collapsed.Count > Excerpt ? collapsed.Skip(Math.Max(Excerpt, collapsed.Count - Excerpt)).ToArray() : Array.Empty<string>(),
                };
            }).ToArray();
        }

        /// <summary>Consecutive identical lines become one line with a repeat count.</summary>
        private static List<string> Collapse(List<string> lines)
        {
            var result = new List<string>();
            for (var i = 0; i < lines.Count;)
            {
                var j = i;
                while (j + 1 < lines.Count && lines[j + 1] == lines[i]) j++;
                result.Add(j > i ? $"{lines[i]} (x{j - i + 1})" : lines[i]);
                i = j + 1;
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(UnityModManagerNet.UnityModManager.Logger), "Write")]
    internal static class UnityModManagerLogWritePatch
    {
        private static bool Prefix(string str)
        {
            Boundaries.Hit("umm-log");
            ModLogSink.Add(str);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class UnityModManagerLogFilePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UnityModManagerNet.UnityModManager.Logger), "WriteBuffers");
            yield return AccessTools.Method(typeof(UnityModManagerNet.UnityModManager.Logger), "Clear");
        }

        private static bool Prefix()
        {
            Boundaries.Hit("umm-log");
            return false;
        }
    }
}
