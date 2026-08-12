using BenchmarkDotNet.Running;
using Dytools.VirtualFileSystem.Benchmarks;

// Run all benchmarks in this assembly.
//
// Usage:
//   dotnet run -c Release               - full benchmark suite
//   dotnet run -c Release -- --list flat - list available benchmarks
//   dotnet run -c Release -- --filter '*CrossNode*' - run subset
//
// NOTE: always use Release configuration. Debug builds disable the JIT
// optimisations that the benchmarks are measuring.
BenchmarkRunner.Run<VfsBenchmarks>(null, args);
