using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// BenchmarkDotNet defaults ArtifactsPath to the current working directory, which varies
// depending on how the project is invoked (e.g. `dotnet run --project` from the repo root
// vs. `dotnet run` from inside the project folder).
// Pinning the path to 3 levels above the assembly (bin/Release/net8.0/ → project root)
// ensures results always land in Benchmark/Anka.Benchmark/BenchmarkDotNet.Artifacts/.
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var config = DefaultConfig.Instance.WithArtifactsPath(Path.Combine(projectRoot, "BenchmarkDotNet.Artifacts"));

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
