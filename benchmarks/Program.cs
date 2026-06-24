using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using RhoMicro.BdnLogging;

namespace Foundatio.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        // Use BenchmarkSwitcher to allow running specific benchmark classes
        // Examples:
        //   dotnet run -c Release                          # Interactive selection
        //   dotnet run -c Release -- --filter *Caching*    # Run only caching benchmarks
        //   dotnet run -c Release -- --filter *Resilience* # Run only resilience benchmarks
        //   dotnet run -c Release -- --filter *DeepClone*  # Run only deep clone benchmarks
        //   dotnet run -c Release -- --list tree           # List all benchmarks

        // The spotlight logger renders a live, cursor-positioned console. When output is
        // redirected (CI, agents, log files) there is no interactive console and its cursor
        // calls throw, so fall back to the default config in that case.
        var config = System.Console.IsOutputRedirected ? DefaultConfig.Instance : SpotlightConfig.Instance;
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
