using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class ConcurrentLoadEvidenceCollectorTests
{
    [Fact]
    public async Task Released_together_demand_does_not_require_physical_call_overlap()
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
        collector.CompleteWave(successful: 1, conflicts: 1, releasedTogether: ready == 2);
        var evidence = collector.Build();

        Assert.Equal(1, evidence.PeakInFlightProductionStoreCalls);
        Assert.Equal(1, evidence.ReleasedTogetherWaveCount);
        Assert.True(evidence.MeetsConfiguredContention(2));
        Assert.True(evidence.MeetsConfiguredContention(2, expectedWaveCount: 1));
        Assert.False(evidence.MeetsConfiguredContention(2, expectedWaveCount: 2));
    }

    [Fact]
    public void Incomplete_wave_is_rejected_before_evidence_can_be_sealed()
    {
        var collector = new ConcurrentLoadEvidenceCollector(requestedParallelism: 2);
        collector.BeginWave();
        using (collector.EnterProductionStoreCall())
        {
        }

        Assert.Throws<InvalidOperationException>(() =>
            collector.CompleteWave(successful: 1, conflicts: 1, releasedTogether: true));
    }

    [Fact]
    public void Wave_not_released_together_makes_sealed_evidence_ineligible()
    {
        var collector = new ConcurrentLoadEvidenceCollector(requestedParallelism: 2);
        collector.BeginWave();
        using (collector.EnterProductionStoreCall())
        {
        }
        using (collector.EnterProductionStoreCall())
        {
        }
        collector.CompleteWave(successful: 1, conflicts: 1, releasedTogether: false);

        var evidence = collector.Build();

        Assert.True(evidence.IsInternallyConsistent());
        Assert.False(evidence.MeetsConfiguredContention(2));
    }

    [Fact]
    public void Arithmetic_tampering_is_rejected()
    {
        var valid = new ConcurrentLoadEvidence(
            RequestedParallelism: 2,
            WaveCount: 2,
            ReleasedTogetherWaveCount: 2,
            Attempts: 4,
            Completions: 4,
            SuccessfulOperations: 2,
            ConflictOperations: 2,
            PeakInFlightProductionStoreCalls: 2);

        Assert.True(valid.MeetsConfiguredContention(2));
        Assert.False((valid with { Attempts = 3 }).IsInternallyConsistent());
        Assert.False((valid with { ReleasedTogetherWaveCount = 1 }).MeetsConfiguredContention(2));
        Assert.False((valid with
        {
            RequestedParallelism = int.MaxValue,
            WaveCount = long.MaxValue,
            ReleasedTogetherWaveCount = long.MaxValue
        }).IsInternallyConsistent());
    }
}
