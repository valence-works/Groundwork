using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.Modules.Inbox;
using Groundwork.Modules.Inbox.Sqlite;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Groundwork.SupportTickets.ExternalModules;
using Microsoft.Data.Sqlite;

namespace Groundwork.SupportTickets;

public sealed class SupportTicketSampleHost : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables;

    private SupportTicketSampleHost(
        StorageManifest manifest,
        IDocumentStore store,
        SupportTicketRepository tickets,
        IInboxStore inbox,
        ExternalModuleFitReport externalModuleFit,
        List<IAsyncDisposable> disposables)
    {
        Manifest = manifest;
        Store = store;
        Tickets = tickets;
        Inbox = inbox;
        ExternalModuleFit = externalModuleFit;
        this.disposables = disposables;
    }

    public StorageManifest Manifest { get; }
    public IDocumentStore Store { get; }
    public SupportTicketRepository Tickets { get; }


    /// <summary>An external module store proving custom capabilities can be wired without core edits.</summary>
    public IInboxStore Inbox { get; }

    /// <summary>
    /// Capability-derived verdict for the externally registered Inbox module.
    /// </summary>
    public ExternalModuleFitReport ExternalModuleFit { get; }

    public static Task<SupportTicketSampleHost> CreateAsync(string connectionString = "Data Source=:memory:") =>
        CreateAsync(new SupportTicketStorageOptions(SupportTicketProvider.Sqlite, connectionString));

    public static async Task<SupportTicketSampleHost> CreateAsync(
        SupportTicketStorageOptions options,
        CancellationToken cancellationToken = default)
    {
        var manifest = SupportTicketManifest.Create();
        var (store, ticketQueries, commentQueries, disposables) = await CreateStoreAsync(options, manifest, cancellationToken);
        var (inbox, externalModuleFit) = await CreateExternalModulesAsync(disposables, cancellationToken);
        return new SupportTicketSampleHost(
            manifest,
            store,
            new SupportTicketRepository(store, ticketQueries, commentQueries),
            inbox,
            externalModuleFit,
            disposables);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in disposables)
            await disposable.DisposeAsync();
    }

    private static async Task<(
        IDocumentStore Store,
        IBoundedDocumentStore TicketQueries,
        IBoundedDocumentStore CommentQueries,
        List<IAsyncDisposable> Disposables)> CreateStoreAsync(
        SupportTicketStorageOptions options,
        StorageManifest manifest,
        CancellationToken cancellationToken)
    {
        var disposables = new List<IAsyncDisposable>();
        switch (options.Provider)
        {
            case SupportTicketProvider.Sqlite:
                {
                    var provider = Provider("groundwork-sqlite");
                    var target = PhysicalSchemaTargetCompiler.Compile(
                        manifest,
                        provider,
                        SqliteGroundworkCapabilities.PhysicalNames);
                    var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                    SqlitePhysicalDocumentStore store;
                    if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:")
                    {
                        var connection = new SqliteConnection(options.ConnectionString);
                        try
                        {
                            store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                                connection,
                                manifest,
                                provider,
                                DocumentStoreAccess.Global,
                                options: AutoApplyOnStartup(),
                                cancellationToken: cancellationToken);
                            disposables.Add(connection);
                        }
                        catch
                        {
                            await connection.DisposeAsync();
                            throw;
                        }
                    }
                    else
                    {
                        store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                            options.ConnectionString,
                            manifest,
                            provider,
                            DocumentStoreAccess.Global,
                            options: AutoApplyOnStartup(),
                            cancellationToken: cancellationToken);
                    }

                    return (
                        store,
                        SqlitePhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.DocumentKind), provider),
                        SqlitePhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.CommentDocumentKind), provider),
                        disposables);
                }
            case SupportTicketProvider.PostgreSql:
                {
                    var provider = Provider("groundwork-postgresql");
                    var target = PhysicalSchemaTargetCompiler.Compile(
                        manifest,
                        provider,
                        PostgreSqlGroundworkCapabilities.PhysicalNames);
                    var store = await PostgreSqlDocumentStoreFactory.OpenPhysicalAsync(
                        options.ConnectionString,
                        manifest,
                        provider,
                        DocumentStoreAccess.Global,
                        options: AutoApplyOnStartup(),
                        cancellationToken: cancellationToken);
                    return (
                        store,
                        PostgreSqlPhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.DocumentKind), provider),
                        PostgreSqlPhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.CommentDocumentKind), provider),
                        disposables);
                }
            case SupportTicketProvider.SqlServer:
                {
                    var provider = Provider("groundwork-sqlserver");
                    var target = PhysicalSchemaTargetCompiler.Compile(
                        manifest,
                        provider,
                        SqlServerGroundworkCapabilities.PhysicalNames);
                    var store = await SqlServerDocumentStoreFactory.OpenPhysicalAsync(
                        options.ConnectionString,
                        manifest,
                        provider,
                        DocumentStoreAccess.Global,
                        options: AutoApplyOnStartup(),
                        cancellationToken: cancellationToken);
                    return (
                        store,
                        SqlServerPhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.DocumentKind), provider),
                        SqlServerPhysicalQueryRuntime.Create(store, manifest, Route(target, SupportTicketManifest.CommentDocumentKind), provider),
                        disposables);
                }
            case SupportTicketProvider.MongoDb:
                {
                    var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                        options.ConnectionString,
                        options.DatabaseName ?? "groundwork_support_tickets",
                        manifest,
                        Provider("groundwork-mongodb"),
                        DocumentStoreAccess.Global,
                        options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true },
                        cancellationToken: cancellationToken);
                    disposables.Add(handle);

                    // The MongoDB physical store is also the bounded-query store for every unit.
                    return (handle.Store, handle.Store, handle.Store, disposables);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unsupported support-ticket provider.");
        }
    }

    private static async Task<(IInboxStore Inbox, ExternalModuleFitReport Fit)> CreateExternalModulesAsync(
        List<IAsyncDisposable> disposables,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        disposables.Insert(0, connection);
        await connection.OpenAsync(cancellationToken);
        await new SqliteInboxMaterializer(connection).MaterializeAsync(cancellationToken);

        return (new SqliteInboxStore(connection), SupportTicketExternalModuleManifest.EvaluateInboxFit());
    }

    private static ProviderIdentity Provider(string name) => new(name, "1.0.0");

    private static GroundworkRuntimeSchemaAdmissionOptions AutoApplyOnStartup() => new() { AutoApplyOnStartup = true };

    private static ExecutableStorageRoute Route(PhysicalSchemaTarget target, string documentKind) =>
        target.Routes.Single(route => route.StorageUnit.Value == documentKind);
}
