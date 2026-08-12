namespace Groundwork.TestInfrastructure;

/// <summary>
/// Single source of truth for provider container images under test. Keep these aligned with
/// <c>benchmarks/Groundwork.PhysicalStorage.Benchmarks/BenchmarkProviderEnvironment.cs</c> so tests
/// and benchmark evidence exercise the same server versions.
/// </summary>
public static class TestContainerImages
{
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04";
    public const string PostgreSql = "postgres:17.6-alpine3.22";
    public const string MongoDb = "mongo:7.0.24";
}
