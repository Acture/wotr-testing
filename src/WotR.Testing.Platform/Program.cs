using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Testing.Extensions;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Services;

namespace WotR.Testing.Platform
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // TRX files are named after this application by default, so several WotR test projects in one invocation
            // would overwrite each other's file; name it after the consumer's test assembly instead.
            var source = Array.IndexOf(args, "--" + WotrOptions.Settings);
            if (source >= 0 && source + 1 < args.Length && args.Contains("--report-trx") && !args.Contains("--report-trx-filename"))
                args = args.Concat(new[] { "--report-trx-filename", WotrOptions.ProjectName(args[source + 1]) + "_{arch}.trx" }).ToArray();
            var builder = await TestApplication.CreateBuilderAsync(args);
            builder.CommandLine.AddProvider(() => new WotrOptions());
#pragma warning disable TPEXP // The platform marks its tree filter experimental; it provides --treenode-filter.
            Microsoft.Testing.Platform.Helpers.TestApplicationBuilderExtensions.AddTreeNodeFilterService(builder, new WotrOptions());
#pragma warning restore TPEXP
            builder.RegisterTestFramework(_ => new TestFrameworkCapabilities(new TrxCapability()),
                (_, services) => new WotrTestFramework(services.GetCommandLineOptions()));
            builder.AddTrxReportProvider();
            using (var application = await builder.BuildAsync())
                return await application.RunAsync();
        }
    }
}
