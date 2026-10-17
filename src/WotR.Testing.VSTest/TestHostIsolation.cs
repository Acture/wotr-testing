using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mono.Cecil;

namespace WotR.Testing.VSTest
{
    /// <summary>Gives the SDK's Json.NET its own assembly identity so Mono cannot bind game references to it.</summary>
    internal static class TestHostIsolation
    {
        private const string JsonName = "Newtonsoft.Json";
        private const string IsolatedName = "WotR.TestHost.Newtonsoft.Json";

        internal sealed class Result
        {
            public string Host;
            public string Manifest;
        }

        public static Result Prepare(string originalHost, string work)
        {
            var sourceDirectory = Path.GetDirectoryName(originalHost);
            var files = Directory.EnumerateFiles(sourceDirectory).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            var originalJson = Path.Combine(sourceDirectory, JsonName + ".dll");
            if (!File.Exists(originalJson)) throw new FileNotFoundException("The SDK testhost's Json.NET dependency was not found.", originalJson);
            // Version the algorithm as well as the inputs. Every host file is hashed; updates cannot reuse an old SDK copy.
            var signature = "wotr-vstest-host-2\n" + string.Join("\n", files.Select(p => Path.GetFileName(p) + ":" + Hash(p)));
            string key;
            using (var sha = SHA256.Create()) key = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(signature)));
            var directory = Path.Combine(work, "vstest-host", key);
            var result = new Result { Host = Path.Combine(directory, Path.GetFileName(originalHost)), Manifest = Path.Combine(directory, "host.xml") };
            if (File.Exists(result.Manifest)) return result;
            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try {
                var manifest = new XElement("TestHost", new XAttribute("originalHost", originalHost),
                    new XAttribute("isolatedHost", result.Host));
                foreach (var file in files) {
                    var filename = Path.GetFileName(file);
                    var destination = Path.Combine(staging, filename == JsonName + ".dll" ? IsolatedName + ".dll" : filename);
                    var changed = false;
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                        try {
                            using (var assembly = AssemblyDefinition.ReadAssembly(file)) {
                                if (assembly.Name.Name == JsonName) {
                                    assembly.Name.Name = IsolatedName;
                                    assembly.Name.PublicKey = Array.Empty<byte>();
                                    assembly.Name.PublicKeyToken = Array.Empty<byte>();
                                    assembly.Name.HasPublicKey = false;
                                    changed = true;
                                }
                                foreach (var reference in assembly.MainModule.AssemblyReferences.Where(r => r.Name == JsonName)) {
                                    reference.Name = IsolatedName;
                                    reference.PublicKeyToken = Array.Empty<byte>();
                                    changed = true;
                                }
                                if (changed) {
                                    assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
                                    assembly.Write(destination);
                                }
                            }
                        }
                        catch (BadImageFormatException) { /* Native helper: copy unchanged. */ }
                    }
                    if (!changed) File.Copy(file, destination);
                    if (file.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) {
                        var config = XDocument.Load(destination);
                        var redirects = config.Descendants().Where(e => e.Name.LocalName == "assemblyIdentity" && (string)e.Attribute("name") == JsonName).ToArray();
                        foreach (var identity in redirects)
                            identity.Parent.Remove(); // The game must retain its own original Json.NET binding.
                        if (redirects.Length > 0) { config.Save(destination); changed = true; }
                    }
                    manifest.Add(new XElement("File", new XAttribute("name", Path.GetFileName(destination)),
                        new XAttribute("originalSha256", Hash(file)), new XAttribute("isolatedSha256", Hash(destination)),
                        new XAttribute("rewritten", changed)));
                }
                new XDocument(manifest).Save(Path.Combine(staging, "host.xml"));
                try { Directory.Move(staging, directory); }
                catch (IOException) when (Directory.Exists(directory)) { /* Another process atomically published this SDK copy. */ }
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            return result;
        }

        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
