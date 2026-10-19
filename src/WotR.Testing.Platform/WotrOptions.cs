using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Testing.Extensions.TrxReport.Abstractions;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace WotR.Testing.Platform
{
    /// <summary>The short WotR options of `dotnet test`. Without --fuzz or --tests both kinds run; --mode narrows the environment.</summary>
    internal sealed class WotrOptions : ICommandLineOptionsProvider
    {
        public const string Mode = "mode";
        public const string Fuzz = "fuzz";
        public const string Tests = "tests";
        public const string FuzzSeed = "fuzz-seed";
        public const string FuzzReplay = "fuzz-replay";
        public const string Settings = "wotr-settings";

        public string Uid => nameof(WotrOptions);
        public string Version => typeof(WotrOptions).Assembly.GetName().Version.ToString();
        public string DisplayName => "WotR.Testing";
        public string Description => "Offgame and in-game tests and automatic interface fuzzing for WotR mods.";
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);

        public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() => new[] {
            new CommandLineOption(Mode, "Environment: auto (default, every environment the tests declare), offgame or ingame.", ArgumentArity.ExactlyOne, false),
            new CommandLineOption(Fuzz, "Run only registered [WotrFuzz] targets.", ArgumentArity.Zero, false),
            new CommandLineOption(Tests, "Run only ordinary tests.", ArgumentArity.Zero, false),
            new CommandLineOption(FuzzSeed, "Seed for every fuzz target, overriding its FuzzOptions.Seed.", ArgumentArity.ExactlyOne, false),
            new CommandLineOption(FuzzReplay, "Replay one saved fuzz case exactly, in the environment where it was recorded.", ArgumentArity.ExactlyOne, false),
            new CommandLineOption(Settings, "Run settings written by the WotR.Testing build targets.", ArgumentArity.ExactlyOne, true)
        };

        public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption option, string[] arguments)
        {
            if (option.Name == Mode && arguments[0] != "auto" && arguments[0] != "offgame" && arguments[0] != "ingame")
                return ValidationResult.InvalidTask("--mode must be auto, offgame or ingame, not " + arguments[0] + ".");
            if (option.Name == FuzzSeed && !int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return ValidationResult.InvalidTask("--fuzz-seed needs an integer.");
            if (option.Name == FuzzReplay && !File.Exists(arguments[0]))
                return ValidationResult.InvalidTask("--fuzz-replay file not found: " + arguments[0]);
            return ValidationResult.ValidTask;
        }

        public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions options)
        {
            if (options.IsOptionSet(Fuzz) && options.IsOptionSet(Tests)) return ValidationResult.InvalidTask("--fuzz and --tests cannot be combined; omit both to run both kinds.");
            if (options.IsOptionSet(Tests) && (options.IsOptionSet(FuzzSeed) || options.IsOptionSet(FuzzReplay)))
                return ValidationResult.InvalidTask("--fuzz-seed and --fuzz-replay cannot be combined with --tests.");
            return ValidationResult.ValidTask;
        }

        /// <summary>The consumer project name, from its settings file name (&lt;assembly&gt;.wotr-settings.json).</summary>
        internal static string ProjectName(string settingsPath)
        {
            var name = Path.GetFileName(settingsPath);
            var suffix = name.IndexOf(".wotr-settings", System.StringComparison.OrdinalIgnoreCase);
            return suffix > 0 ? name.Substring(0, suffix) : Path.GetFileNameWithoutExtension(name);
        }
    }

    internal sealed class TrxCapability : ITrxReportCapability
    {
        public bool IsSupported => true;
        public void Enable() { }
    }
}
