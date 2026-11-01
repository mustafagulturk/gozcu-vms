using BenchmarkDotNet.Running;

namespace Gozcu.Benchmarks;

public static class BenchmarkEntryPoint
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEntryPoint).Assembly).Run(args);
}
