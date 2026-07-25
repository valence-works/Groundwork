using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class ConcurrentLoadEvidenceCollectorTests
{
    [Fact]
    public async Task Pre_call_readiness_does_not_inflate_production_store_call_peak()
    {
        var collector = new ConcurrentLoadEvidenceCollector(requestedParallelism: 2);
        collector.BeginWave();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serialProductionStore = new SemaphoreSlim(1, 1);
        var ready = 0;

        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            if (Interlocked.Increment(ref ready) == 2)
                release.TrySetResult();
            await release.Task;
            await serialProductionStore.WaitAsync();
            try
            {
                using (collector.EnterProductionStoreCall())
                    await Task.Yield();
            }
            finally
            {
                serialProductionStore.Release();
            }
        });

        await Task.WhenAll(attempts);
        collector.CompleteWave(successful: 1, conflicts: 1);
        var evidence = collector.Build();

        Assert.Equal(1, evidence.PeakInFlightProductionStoreCalls);
        Assert.Equal(0, evidence.FullyParallelWaveCount);
        Assert.False(evidence.MeetsConfiguredParallelism(2));
    }

    [Fact]
    public void Incomplete_wave_is_rejected_before_evidence_can_be_sealed()
    {
        var collector = new ConcurrentLoadEvidenceCollector(requestedParallelism: 2);
        collector.BeginWave();
        using (collector.EnterProductionStoreCall())
        {
        }

        Assert.Throws<InvalidOperationException>(() => collector.CompleteWave(successful: 1, conflicts: 1));
    }

    [Fact]
    public void Under_parallel_wave_makes_sealed_evidence_ineligible()
    {
        var collector = new ConcurrentLoadEvidenceCollector(requestedParallelism: 2);
        collector.BeginWave();
        using (collector.EnterProductionStoreCall())
        {
        }
        using (collector.EnterProductionStoreCall())
        {
        }
        collector.CompleteWave(successful: 1, conflicts: 1);

        var evidence = collector.Build();

        Assert.True(evidence.IsInternallyConsistent());
        Assert.False(evidence.MeetsConfiguredParallelism(2));
    }

    [Fact]
    public void Arithmetic_tampering_is_rejected()
    {
        var valid = new ConcurrentLoadEvidence(
            RequestedParallelism: 2,
            WaveCount: 2,
            FullyParallelWaveCount: 2,
            Attempts: 4,
            Completions: 4,
            SuccessfulOperations: 2,
            ConflictOperations: 2,
            PeakInFlightProductionStoreCalls: 2);

        Assert.True(valid.MeetsConfiguredParallelism(2));
        Assert.False((valid with { Attempts = 3 }).IsInternallyConsistent());
        Assert.False((valid with { FullyParallelWaveCount = 1 }).MeetsConfiguredParallelism(2));
        Assert.False((valid with
        {
            RequestedParallelism = int.MaxValue,
            WaveCount = long.MaxValue,
            FullyParallelWaveCount = long.MaxValue
        }).IsInternallyConsistent());
    }
}
