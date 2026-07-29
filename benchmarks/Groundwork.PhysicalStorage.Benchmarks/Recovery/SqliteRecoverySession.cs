using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>Owns the benchmark recovery client while exposing only public store contracts.</summary>
internal sealed class SqliteRecoverySession(IDocumentStore store) : IAsyncDisposable
{
    public IDocumentStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
