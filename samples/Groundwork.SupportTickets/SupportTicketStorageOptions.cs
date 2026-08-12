using Microsoft.Extensions.Configuration;

namespace Groundwork.SupportTickets;

public sealed record SupportTicketStorageOptions(
    SupportTicketProvider Provider,
    string ConnectionString,
    string? DatabaseName = null)
{
    public static SupportTicketStorageOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Groundwork");
        var provider = Enum.TryParse<SupportTicketProvider>(section["Provider"], ignoreCase: true, out var parsedProvider)
            ? parsedProvider
            : SupportTicketProvider.Sqlite;
        var connectionString = section["ConnectionString"] ?? configuration.GetConnectionString("Groundwork")
            ?? (provider == SupportTicketProvider.Sqlite ? "Data Source=support-tickets.db" : null)
            ?? throw new InvalidOperationException($"A connection string must be configured for provider '{provider}'.");

        return new SupportTicketStorageOptions(provider, connectionString, section["DatabaseName"]);
    }
}

public enum SupportTicketProvider
{
    Sqlite,
    PostgreSql,
    SqlServer,
    MongoDb
}
