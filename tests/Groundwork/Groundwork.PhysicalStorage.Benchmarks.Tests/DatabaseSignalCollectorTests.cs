using System.Diagnostics;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

[Collection(SqliteObservableEvidenceCollection.Name)]
public sealed class DatabaseSignalCollectorTests
{
    [Fact]
    public void Relational_command_signals_are_scoped_to_the_measured_target()
    {
        const string measuredPath = "/tmp/groundwork-measured-signal.db";
        const string otherPath = "/tmp/groundwork-other-signal.db";
        using var collector = new DatabaseSignalCollector();
        using var listener = new DiagnosticListener("Microsoft.Data.Sqlite");
        using var measuredConnection = new SqliteConnection($"Data Source={measuredPath}");
        using var otherConnection = new SqliteConnection($"Data Source={otherPath}");
        using var measuredCommand = measuredConnection.CreateCommand();
        using var otherCommand = otherConnection.CreateCommand();
        using var measurement = collector.BeginMeasurement(DatabaseSignalTarget.ForSqlite(measuredPath));

        listener.Write("CommandStart", new { Command = measuredCommand });
        listener.Write("CommandStart", new { Command = otherCommand });

        var signals = measurement.Complete();

        Assert.Equal(1, signals.CommandStarts);
        Assert.Equal(1, signals.ObservableRoundTrips);
        Assert.Equal(DatabaseSignalAvailability.Observed, signals.Evidence.Availability);
        Assert.Equal("target-scoped-diagnostic-command", signals.Evidence.Source);
        Assert.DoesNotContain(signals.ToProviderWork(), pair => pair.Value == 0);
    }

    [Theory]
    [InlineData(BenchmarkProvider.SqlServer)]
    [InlineData(BenchmarkProvider.PostgreSql)]
    public void Relational_provider_selector_counts_only_the_measured_target(BenchmarkProvider provider)
    {
        using var collector = new DatabaseSignalCollector();
        using var listener = new DiagnosticListener(provider == BenchmarkProvider.SqlServer ? "Microsoft.Data.SqlClient" : "Npgsql");
        using var measured = CreateRelationalConnection(provider, measured: true);
        using var other = CreateRelationalConnection(provider, measured: false);
        using var measuredCommand = measured.CreateCommand();
        using var otherCommand = other.CreateCommand();
        using var measurement = collector.BeginMeasurement(CreateRelationalTarget(provider));

        listener.Write("CommandStart", new { Command = measuredCommand });
        listener.Write("CommandStart", new { Command = otherCommand });

        var signals = measurement.Complete();

        Assert.Equal(1, signals.CommandStarts);
        Assert.Equal(1, signals.ObservableRoundTrips);
        Assert.Equal(DatabaseSignalAvailability.Observed, signals.Evidence.Availability);
        Assert.Equal("target-scoped-diagnostic-command", signals.Evidence.Source);
        Assert.DoesNotContain(signals.ToProviderWork(), pair => pair.Value == 0);
    }

    [Theory]
    [InlineData(BenchmarkProvider.SqlServer)]
    [InlineData(BenchmarkProvider.PostgreSql)]
    public void Relational_provider_selector_rejects_a_different_target(BenchmarkProvider provider)
    {
        using var collector = new DatabaseSignalCollector();
        using var listener = new DiagnosticListener(provider == BenchmarkProvider.SqlServer ? "Microsoft.Data.SqlClient" : "Npgsql");
        using var other = CreateRelationalConnection(provider, measured: false);
        using var otherCommand = other.CreateCommand();
        using var measurement = collector.BeginMeasurement(CreateRelationalTarget(provider));

        listener.Write("CommandStart", new { Command = otherCommand });

        var signals = measurement.Complete();

        Assert.Null(signals.CommandStarts);
        Assert.Null(signals.ObservableRoundTrips);
        Assert.Equal(DatabaseSignalAvailability.Unavailable, signals.Evidence.Availability);
        Assert.Equal("no-target-scoped-provider-telemetry", signals.Evidence.Reason);
        Assert.DoesNotContain(signals.ToProviderWork(), pair => pair.Value == 0);
    }

    [Fact]
    public void Mongo_client_activity_is_scoped_to_the_measured_database()
    {
        using var collector = new DatabaseSignalCollector();
        using var source = new ActivitySource("MongoDB.Driver.Core.Extensions.DiagnosticSources");
        using var measurement = collector.BeginMeasurement(DatabaseSignalTarget.ForMongoDb("groundwork_measured"));

        using (var other = source.StartActivity("mongo.command", ActivityKind.Client))
            other!.SetTag("db.namespace", "groundwork_other.documents");
        using (var measured = source.StartActivity("mongo.command", ActivityKind.Client))
            measured!.SetTag("db.namespace", "groundwork_measured.documents");

        var signals = measurement.Complete();

        Assert.Null(signals.CommandStarts);
        Assert.Equal(1, signals.ClientActivities);
        Assert.Equal(1, signals.ObservableRoundTrips);
        Assert.Equal(DatabaseSignalAvailability.Observed, signals.Evidence.Availability);
        Assert.Equal("target-scoped-client-activity", signals.Evidence.Source);
    }

    [Fact]
    public void Signal_evidence_never_serializes_connection_values_or_secrets()
    {
        const string secret = "super-secret-password";
        using var collector = new DatabaseSignalCollector();
        using var listener = new DiagnosticListener("Npgsql");
        using var connection = new NpgsqlConnection(
            $"Host=localhost;Database=groundwork;Username=groundwork;Password={secret};Application Name=groundwork-measured");
        using var command = connection.CreateCommand();
        using var measurement = collector.BeginMeasurement(DatabaseSignalTarget.ForPostgreSql("groundwork-measured"));

        listener.Write("CommandStart", new { Command = command });

        var json = JsonSerializer.Serialize(measurement.Complete().Evidence, BenchmarkJson.Options);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", json, StringComparison.Ordinal);
        Assert.DoesNotContain("groundwork-measured", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_benchmark_target_uses_its_selector_application_name_on_production_connections()
    {
        var target = new PostgreSqlBenchmarkTarget(
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.SharedDocuments,
            "selector-proof",
            "Host=localhost;Database=groundwork;Username=groundwork;Password=secret;Application Name=wrong-target",
            migrationDatasetSize: 1,
            sourceDescription: "test");
        using var connection = new NpgsqlConnection(target.CreateProductionConnectionString());
        using var command = connection.CreateCommand();

        Assert.Equal(target.SignalApplicationName, new NpgsqlConnectionStringBuilder(connection.ConnectionString).ApplicationName);
        Assert.True(target.SignalTarget.MatchesCommand(command));
    }

    private static DatabaseSignalTarget CreateRelationalTarget(BenchmarkProvider provider) => provider switch
    {
        BenchmarkProvider.SqlServer => DatabaseSignalTarget.ForSqlServer("groundwork_measured"),
        BenchmarkProvider.PostgreSql => DatabaseSignalTarget.ForPostgreSql("groundwork-measured"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private static DbConnection CreateRelationalConnection(BenchmarkProvider provider, bool measured) => provider switch
    {
        BenchmarkProvider.SqlServer => new SqlConnection(
            $"Server=localhost;Database={(measured ? "groundwork_measured" : "groundwork_other")};User Id=groundwork;Password=secret"),
        BenchmarkProvider.PostgreSql => new NpgsqlConnection(
            $"Host=localhost;Database=groundwork;Username=groundwork;Password=secret;Application Name={(measured ? "groundwork-measured" : "groundwork-other")}"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
