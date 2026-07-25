namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

internal static class BenchmarkSampleEvidence
{
    /// <summary>
    /// Test-only fixture helper for samples whose round-trip metric is explicitly
    /// meant to model a target-scoped diagnostic command observation.
    /// </summary>
    public static BenchmarkSample WithObservedCommandSignal(this BenchmarkSample sample)
    {
        if (sample.RoundTrips is not > 0)
            throw new ArgumentException("Observed command fixtures require a positive round-trip count.", nameof(sample));

        return sample with
        {
            DatabaseSignal = new DatabaseSignalEvidence(
                DatabaseSignalAvailability.Observed,
                "target-scoped-diagnostic-command",
                null,
                sample.RoundTrips,
                null,
                sample.RoundTrips)
        };
    }
}
