using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.Modules.Inbox;
using Groundwork.Modules.Inbox.Sqlite;
using Groundwork.PostgreSql.Documents;
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
        var manifest = SupportTicketManifest.Create(options.EffectivePhysicalization, options.EffectivePhysicalizedIndexes);
        var (store, disposables) = await CreateStoreAsync(options, manifest, cancellationToken);
        var (inbox, externalModuleFit) = await CreateExternalModulesAsync(disposables, cancellationToken);
        return new SupportTicketSampleHost(manifest, store, new SupportTicketRepository(store), inbox, externalModuleFit, disposables);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in disposables)
            await disposable.DisposeAsync();
    }

    private static async Task<(IDocumentStore Store, List<IAsyncDisposable> Disposables)> CreateStoreAsync(
        SupportTicketStorageOptions options,
        StorageManifest manifest,
        CancellationToken cancellationToken)
    {
        var disposables = new List<IAsyncDisposable>();
        switch (options.Provider)
        {
            case SupportTicketProvider.Sqlite:
                {
                    var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                    if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:")
                    {
                        var connection = new SqliteConnection(options.ConnectionString);
                        try
                        {
                            var memoryStore = await SqliteDocumentStoreFactory.CreateAsync(
                                connection,
                                manifest,
                                Provider("groundwork-sqlite"),
                                Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                                cancellationToken: cancellationToken);
                            disposables.Add(connection);
                            return (memoryStore, disposables);
                        }
                        catch
                        {
                            await connection.DisposeAsync();
                            throw;
                        }
                    }

                    var store = await SqliteDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-sqlite"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.PostgreSql:
                {
                    var store = await PostgreSqlDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-postgresql"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.SqlServer:
                {
                    var store = await SqlServerDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-sqlserver"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.MongoDb:
                {
                    var handle = await MongoDbDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        options.DatabaseName ?? "groundwork_support_tickets",
                        manifest,
                        Provider("groundwork-mongodb"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    disposables.Add(handle);
                    return (handle.Store, disposables);
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
}
