using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Groundwork.PhysicalStorage.Benchmarks;

public enum DatabaseSignalAvailability
{
    Observed,
    Unavailable
}

/// <summary>
/// Artifact-safe description of target-scoped provider telemetry. Connection values, target names,
/// and diagnostic payloads never leave the in-process selector.
/// </summary>
public sealed record DatabaseSignalEvidence(
    DatabaseSignalAvailability Availability,
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? CommandStarts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? ClientActivities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? ObservableRoundTrips)
{
    public static bool IsRecognizedObservedSource(string? source) =>
        source is "target-scoped-diagnostic-command" or "target-scoped-client-activity";
}

public sealed record DatabaseSignalSnapshot(
    long? CommandStarts,
    long? ClientActivities,
    DatabaseSignalEvidence Evidence)
{
    public long? ObservableRoundTrips => Evidence.ObservableRoundTrips;

    public IReadOnlyDictionary<string, long> ToProviderWork()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        if (CommandStarts.HasValue)
            values["target_scoped_diagnostic_command_starts"] = CommandStarts.Value;
        if (ClientActivities.HasValue)
            values["target_scoped_client_activities"] = ClientActivities.Value;
        if (ObservableRoundTrips.HasValue)
            values["target_scoped_round_trip_signals"] = ObservableRoundTrips.Value;
        if (Evidence.Availability == DatabaseSignalAvailability.Unavailable)
            values["target_scoped_provider_telemetry_unavailable"] = 1;
        return values;
    }
}

/// <summary>
/// A non-serializable selector that accepts only telemetry emitted for the target being measured.
/// It intentionally retains the matching connection/database details in private fields.
/// </summary>
public sealed class DatabaseSignalTarget
{
    private readonly BenchmarkProvider provider;
    private readonly string target;

    private DatabaseSignalTarget(BenchmarkProvider provider, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("A database signal target is required.", nameof(target));
        this.provider = provider;
        this.target = target;
    }

    public static DatabaseSignalTarget ForSqlite(string dataSource) =>
        new(BenchmarkProvider.Sqlite, NormalizeSqliteDataSource(dataSource));

    public static DatabaseSignalTarget ForSqlServer(string databaseName) =>
        new(BenchmarkProvider.SqlServer, databaseName);

    public static DatabaseSignalTarget ForPostgreSql(string applicationName) =>
        new(BenchmarkProvider.PostgreSql, applicationName);

    public static DatabaseSignalTarget ForMongoDb(string databaseName) =>
        new(BenchmarkProvider.MongoDb, databaseName);

    internal bool MatchesCommand(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var connection = command.Connection;
        if (connection is null)
            return false;

        return provider switch
        {
            BenchmarkProvider.Sqlite when connection is SqliteConnection =>
                string.Equals(
                    NormalizeSqliteDataSource(connection.DataSource),
                    target,
                    StringComparison.Ordinal),
            BenchmarkProvider.SqlServer when connection is SqlConnection =>
                string.Equals(connection.Database, target, StringComparison.OrdinalIgnoreCase),
            BenchmarkProvider.PostgreSql when connection is NpgsqlConnection =>
                string.Equals(GetApplicationName(connection.ConnectionString), target, StringComparison.Ordinal),
            _ => false
        };
    }

    internal bool MatchesActivity(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (provider == BenchmarkProvider.MongoDb)
        {
            if (!activity.Source.Name.Contains("MongoDB", StringComparison.OrdinalIgnoreCase))
                return false;
            var databaseNamespace = Tag(activity, "db.namespace") ?? Tag(activity, "db.name");
            return string.Equals(databaseNamespace, target, StringComparison.Ordinal) ||
                   databaseNamespace?.StartsWith(target + ".", StringComparison.Ordinal) == true;
        }

        if (!IsProviderSource(activity.Source.Name))
            return false;
        var connectionString = Tag(activity, "db.connection_string") ??
                               Tag(activity, "db.connectionString") ??
                               Tag(activity, "connection_string");
        return connectionString is not null && MatchesConnectionString(connectionString);
    }

    private bool MatchesConnectionString(string connectionString)
    {
        try
        {
            return provider switch
            {
                BenchmarkProvider.Sqlite => string.Equals(
                    NormalizeSqliteDataSource(new SqliteConnectionStringBuilder(connectionString).DataSource),
                    target,
                    StringComparison.Ordinal),
                BenchmarkProvider.SqlServer => string.Equals(
                    new SqlConnectionStringBuilder(connectionString).InitialCatalog,
                    target,
                    StringComparison.OrdinalIgnoreCase),
                BenchmarkProvider.PostgreSql => string.Equals(
                    GetApplicationName(connectionString),
                    target,
                    StringComparison.Ordinal),
                _ => false
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsProviderSource(string source) => provider switch
    {
        BenchmarkProvider.Sqlite => source.Contains("Sqlite", StringComparison.OrdinalIgnoreCase),
        BenchmarkProvider.SqlServer => source.Contains("SqlClient", StringComparison.OrdinalIgnoreCase),
        BenchmarkProvider.PostgreSql => source.Contains("Npgsql", StringComparison.OrdinalIgnoreCase),
        BenchmarkProvider.MongoDb => source.Contains("MongoDB", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string? Tag(Activity activity, string name) =>
        activity.GetTagItem(name)?.ToString();

    private static string GetApplicationName(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).ApplicationName ?? string.Empty;

    private static string NormalizeSqliteDataSource(string dataSource) =>
        string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? dataSource
            : Path.GetFullPath(dataSource);
}

public sealed class DatabaseSignalCollector :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>,
    IDisposable
{
    private readonly ConcurrentBag<IDisposable> subscriptions = [];
    private readonly IDisposable allListenersSubscription;
    private readonly ActivityListener activityListener;
    private readonly AsyncLocal<MeasurementScope?> activeMeasurement = new();

    public DatabaseSignalCollector()
    {
        allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        activityListener = new ActivityListener
        {
            ShouldListenTo = static source => IsDatabaseSource(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activeMeasurement.Value?.TryRecordClientActivity(activity)
        };
        ActivitySource.AddActivityListener(activityListener);
    }

    public MeasurementScope BeginMeasurement(DatabaseSignalTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (activeMeasurement.Value is not null)
            throw new InvalidOperationException("A database signal measurement is already active on this execution context.");
        var measurement = new MeasurementScope(this, target);
        activeMeasurement.Value = measurement;
        return measurement;
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (IsDatabaseSource(listener.Name))
            subscriptions.Add(listener.Subscribe(this, IsCommandStartEvent));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (IsCommandStartEvent(value.Key))
            activeMeasurement.Value?.TryRecordCommand(value.Value);
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        activeMeasurement.Value = null;
        activityListener.Dispose();
        allListenersSubscription.Dispose();
        while (subscriptions.TryTake(out var subscription))
            subscription.Dispose();
    }

    public static bool IsCommandStartEvent(string eventName) =>
        eventName.Contains("Command", StringComparison.OrdinalIgnoreCase) &&
        (eventName.EndsWith("Before", StringComparison.OrdinalIgnoreCase) ||
         eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
         eventName.EndsWith("Started", StringComparison.OrdinalIgnoreCase));

    private static bool IsDatabaseSource(string source) =>
        source.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("MongoDB", StringComparison.OrdinalIgnoreCase);

    public sealed class MeasurementScope : IDisposable
    {
        private readonly DatabaseSignalCollector owner;
        private readonly DatabaseSignalTarget target;
        private long commandStarts;
        private long clientActivities;
        private DatabaseSignalSnapshot? snapshot;
        private int disposed;

        internal MeasurementScope(DatabaseSignalCollector owner, DatabaseSignalTarget target)
        {
            this.owner = owner;
            this.target = target;
        }

        internal void TryRecordCommand(object? payload)
        {
            if (TryGetCommand(payload) is { } command && target.MatchesCommand(command))
                Interlocked.Increment(ref commandStarts);
        }

        internal void TryRecordClientActivity(Activity activity)
        {
            if (activity.Kind == ActivityKind.Client && target.MatchesActivity(activity))
                Interlocked.Increment(ref clientActivities);
        }

        public DatabaseSignalSnapshot Complete()
        {
            Dispose();
            return snapshot ??= CreateSnapshot(
                Interlocked.Read(ref commandStarts),
                Interlocked.Read(ref clientActivities));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && ReferenceEquals(owner.activeMeasurement.Value, this))
                owner.activeMeasurement.Value = null;
        }

        private static DatabaseSignalSnapshot CreateSnapshot(long commandStarts, long clientActivities)
        {
            long? observedCommands = commandStarts > 0 ? commandStarts : null;
            long? observedActivities = clientActivities > 0 ? clientActivities : null;
            var observableRoundTrips = observedCommands ?? observedActivities;
            var availability = observableRoundTrips.HasValue
                ? DatabaseSignalAvailability.Observed
                : DatabaseSignalAvailability.Unavailable;
            var source = observedCommands.HasValue
                ? "target-scoped-diagnostic-command"
                : observedActivities.HasValue
                    ? "target-scoped-client-activity"
                    : "unavailable";
            return new DatabaseSignalSnapshot(
                observedCommands,
                observedActivities,
                new DatabaseSignalEvidence(
                    availability,
                    source,
                    availability == DatabaseSignalAvailability.Unavailable
                        ? "no-target-scoped-provider-telemetry"
                        : null,
                    observedCommands,
                    observedActivities,
                    observableRoundTrips));
        }

        private static DbCommand? TryGetCommand(object? payload) =>
            payload?.GetType().GetProperty("Command")?.GetValue(payload) as DbCommand;
    }
}
