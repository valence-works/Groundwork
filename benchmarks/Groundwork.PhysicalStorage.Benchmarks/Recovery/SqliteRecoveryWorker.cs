using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class SqliteRecoveryWorker
{
    internal static async Task RunMutationAsync(RecoveryWorkerRequest request, CancellationToken cancellationToken)
    {
        RequireVersion(request.ProtocolVersion);
        RecoveryProtocol.RequireCurrentSource(request.Source);
        await using var session = await SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            request.StorageForm, request.Instance, request.DatabasePath, cancellationToken);
        await using var unitOfWork = await session.Store.BeginAsync(
            DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind), cancellationToken);
        var staged = await unitOfWork.SaveAsync(
            RecoveryProtocol.Save(RecoveryProtocol.Content("closed", 2), expectedVersion: 1), cancellationToken);
        if (staged.Status != DocumentStoreWriteStatus.Saved)
            throw new InvalidOperationException($"Recovery mutation staging returned {staged.Status}.");
        if (request.FailurePoint == RecoveryFailurePoint.PreCommit)
        {
            await SignalAndBlockAsync(request, "staged", cancellationToken);
            return;
        }
        await unitOfWork.CommitAsync(cancellationToken);
        await SignalAndBlockAsync(request, "committed", cancellationToken);
    }

    internal static async Task VerifyAsync(RecoveryVerificationRequest request, CancellationToken cancellationToken)
    {
        RequireVersion(request.ProtocolVersion);
        RecoveryProtocol.RequireCurrentSource(request.Source);
        await using var session = await SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            request.StorageForm, request.Instance, request.DatabasePath, cancellationToken);
        var document = await session.Store.LoadAsync(BenchmarkModelFactory.DocumentKind, RecoveryProtocol.DocumentId, cancellationToken)
            ?? throw new InvalidOperationException("Recovery verification could not load the seeded document.");
        DocumentStoreWriteStatus? retry = null;
        if (request.FailurePoint == RecoveryFailurePoint.CommittedBeforeAcknowledgement)
        {
            retry = (await session.Store.SaveAsync(
                RecoveryProtocol.Save(RecoveryProtocol.Content("closed", 2), expectedVersion: 1), cancellationToken)).Status;
            if (retry != DocumentStoreWriteStatus.ConcurrencyConflict)
                throw new InvalidOperationException($"Stale recovery retry returned {retry}.");
            document = await session.Store.LoadAsync(BenchmarkModelFactory.DocumentKind, RecoveryProtocol.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Recovery retry removed the durable document.");
        }
        await RecoveryProtocol.WriteAsync(request.ResultPath, new RecoveryVerificationResult(
            RecoveryProtocol.Version,
            request.Source,
            request.FailurePoint,
            Environment.ProcessId,
            document.Version,
            RecoveryProtocol.StateDigest(document),
            retry == DocumentStoreWriteStatus.ConcurrencyConflict ? "concurrencyConflict" : "notAttempted"), cancellationToken);
    }

    private static async Task SignalAndBlockAsync(RecoveryWorkerRequest request, string state, CancellationToken cancellationToken)
    {
        await RecoveryProtocol.WriteAsync(request.BarrierPath, new RecoveryBarrier(
            RecoveryProtocol.Version,
            request.Source,
            request.FailurePoint,
            Environment.ProcessId,
            state), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static void RequireVersion(string version)
    {
        if (version != RecoveryProtocol.Version)
            throw new InvalidOperationException("Recovery request has an unsupported protocol version.");
    }
}
