using BenchmarkDotNet.Running;

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
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
