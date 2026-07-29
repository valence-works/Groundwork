using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class SqliteRecoveryWorker
{
    internal static async Task RunMutationAsync(RecoveryWorkerRequest request, CancellationToken cancellationToken)
    {
        RequireVersion(request.ProtocolVersion);
        RecoveryProtocol.RequireCurrentSource(request.Source);
        var store = await SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            request.StorageForm, request.Instance, request.DatabasePath, cancellationToken);
        await using var unitOfWork = await store.BeginAsync(
            DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind), cancellationToken);
        var staged = await unitOfWork.SaveAsync(
            RecoveryProtocol.Save(RecoveryProtocol.Content("closed", 2), expectedVersion: 1), cancellationToken);
        if (staged.Status != DocumentStoreWriteStatus.Saved)
            throw new InvalidOperationException($"Recovery mutation staging returned {staged.Status}.");
        if (request.FailurePoint == RecoveryFailurePoint.PreCommit)
        {
            await SignalAsync(request, "staged-pre-commit", cancellationToken);
            await RecoveryProtocol.WaitForFileAsync(request.ResponseReleasePath, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
        }
        else
        {
            await unitOfWork.CommitAsync(cancellationToken);
            await SignalAsync(request, "committed-before-requester-acknowledgement", cancellationToken);
            await RecoveryProtocol.WaitForFileAsync(request.ResponseReleasePath, cancellationToken);
        }
        await RecoveryProtocol.WriteAsync(request.RequesterAcknowledgementPath, new RecoveryRequesterAcknowledgement(
            RecoveryProtocol.Version,
            request.Source,
            request.FailurePoint,
            Environment.ProcessId), cancellationToken);
    }

    internal static async Task VerifyAsync(RecoveryVerificationRequest request, CancellationToken cancellationToken)
    {
        RequireVersion(request.ProtocolVersion);
        RecoveryProtocol.RequireCurrentSource(request.Source);
        var store = await SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            request.StorageForm, request.Instance, request.DatabasePath, cancellationToken);
        var document = await store.LoadAsync(BenchmarkModelFactory.DocumentKind, RecoveryProtocol.DocumentId, cancellationToken)
            ?? throw new InvalidOperationException("Recovery verification could not load the seeded document.");
        var beforeRetryVersion = document.Version;
        var beforeRetryDigest = RecoveryProtocol.StateDigest(document);
        var retry = (await store.SaveAsync(
            RecoveryProtocol.Save(RecoveryProtocol.Content("closed", 2), expectedVersion: 1), cancellationToken)).Status;
        var expectedRetry = request.FailurePoint == RecoveryFailurePoint.PreCommit
            ? DocumentStoreWriteStatus.Saved
            : DocumentStoreWriteStatus.ConcurrencyConflict;
        if (retry != expectedRetry)
            throw new InvalidOperationException($"Recovery retry returned {retry}; expected {expectedRetry}.");
        document = await store.LoadAsync(BenchmarkModelFactory.DocumentKind, RecoveryProtocol.DocumentId, cancellationToken)
            ?? throw new InvalidOperationException("Recovery retry removed the durable document.");
        await RecoveryProtocol.WriteAsync(request.ResultPath, new RecoveryVerificationResult(
            RecoveryProtocol.Version,
            request.Source,
            request.FailurePoint,
            Environment.ProcessId,
            beforeRetryVersion,
            beforeRetryDigest,
            retry == DocumentStoreWriteStatus.Saved ? "saved" : "concurrencyConflict",
            document.Version,
            RecoveryProtocol.StateDigest(document)), cancellationToken);
    }

    private static Task SignalAsync(RecoveryWorkerRequest request, string state, CancellationToken cancellationToken)
    {
        return RecoveryProtocol.WriteAsync(request.InstrumentationBarrierPath, new RecoveryInstrumentationBarrier(
            RecoveryProtocol.Version,
            request.Source,
            request.FailurePoint,
            Environment.ProcessId,
            state), cancellationToken);
    }

    private static void RequireVersion(string version)
    {
        if (version != RecoveryProtocol.Version)
            throw new InvalidOperationException("Recovery request has an unsupported protocol version.");
    }
}
