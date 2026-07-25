namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public abstract class BenchmarkIntegrationTestBase(string outputPrefix) : IAsyncDisposable
{
    protected string Output { get; } =
        Path.Combine(Path.GetTempPath(), $"{outputPrefix}-{Guid.NewGuid():N}");

    protected static string RepositoryRoot { get; } = FindRepositoryRoot();

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Output))
            Directory.Delete(Output, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }
}
