using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WotR.Testing.MonoHost
{
    /// <summary>
    /// Runs a .NET program on the Unity Mono runtime of the user's own WotR installation.
    /// </summary>
    /// <remarks>
    /// The game ships Mono as a library (MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll) that exports mono_main, the
    /// entry point of mono.exe. This host loads that library from the installation, points it at the given class library
    /// folder and configuration, and passes the remaining arguments to mono_main. The program then runs entirely on the
    /// game's runtime; this .NET Framework process only starts it. Nothing is copied out of the installation.
    ///
    /// Usage: wotr-mono-host --runtime &lt;EmbedRuntime dir&gt; --config &lt;etc dir&gt; --assemblies &lt;dir&gt; -- &lt;program.exe&gt; [args...]
    /// </remarks>
    internal static class Program
    {
        private const string MonoLibrary = "mono-2.0-bdwgc.dll";
        private const uint LoadWithAlteredSearchPath = 0x00000008;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MonoSetDirs(IntPtr assemblyDirectory, IntPtr configDirectory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MonoSetAssembliesPath(IntPtr path);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MonoMain(int argc, IntPtr argv);

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (HostException error)
            {
                Console.Error.WriteLine($"wotr-mono-host: {error.Message}");
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            var options = new Dictionary<string, string>();
            var separator = Array.IndexOf(args, "--");
            var named = separator < 0 ? args : args.Take(separator).ToArray();
            for (var i = 0; i < named.Length; i += 2)
            {
                if (i + 1 >= named.Length || !named[i].StartsWith("--", StringComparison.Ordinal)) throw new HostException($"unexpected argument {named[i]}");
                options[named[i]] = named[i + 1];
            }
            string Required(string name) => options.TryGetValue(name, out var value) ? value : throw new HostException($"missing {name}");
            var runtime = Required("--runtime");
            var config = Required("--config");
            var assemblies = Required("--assemblies");
            var program = separator < 0 ? Array.Empty<string>() : args.Skip(separator + 1).ToArray();
            if (program.Length == 0) throw new HostException("missing program after --");

            var library = Path.GetFullPath(Path.Combine(runtime, MonoLibrary));
            if (!File.Exists(library)) throw new HostException($"[ENV_MISSING] Unity Mono runtime not found: {library}");
            var module = LoadLibraryEx(library, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero) throw new HostException($"cannot load {library}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            // Mono keeps these pointers; they live until the process exits.
            var assembliesPath = Utf8(MonoPath(assemblies));
            Function<MonoSetDirs>(module, "mono_set_dirs")(assembliesPath, Utf8(MonoPath(config)));
            Function<MonoSetAssembliesPath>(module, "mono_set_assemblies_path")(assembliesPath);

            var argv = new[] { "mono" }.Concat(program).Select(Utf8).Append(IntPtr.Zero).ToArray();
            var block = Marshal.AllocHGlobal(IntPtr.Size * argv.Length);
            for (var i = 0; i < argv.Length; i++) Marshal.WriteIntPtr(block, i * IntPtr.Size, argv[i]);
            return Function<MonoMain>(module, "mono_main")(argv.Length - 1, block);
        }

        private static string MonoPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

        private static T Function<T>(IntPtr module, string name) where T : Delegate
        {
            var address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new HostException($"{name} is not exported by the Mono runtime");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static IntPtr Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private sealed class HostException : Exception
        {
            public HostException(string message) : base(message) { }
        }
    }
}
