namespace Groundwork.Relational.Documents;

/// <summary>
/// The durable physical schema no longer matches the routes compiled into a document-store writer.
/// The caller must rebuild the store from the currently applied manifest before retrying.
/// </summary>
public sealed class PhysicalSchemaUpgradeRequiredException : InvalidOperationException
{
    public const string DiagnosticCode = "GW-RELATIONAL-SCHEMA-001";

    public PhysicalSchemaUpgradeRequiredException(string manifestIdentity, string providerName)
        : this(manifestIdentity, providerName, null)
    {
    }

    public PhysicalSchemaUpgradeRequiredException(
        string manifestIdentity,
        string providerName,
        Exception? innerException)
        : base(
            $"{DiagnosticCode}: Physical schema upgrade required for manifest '{manifestIdentity}' and provider " +
            $"'{providerName}'; the writer's compiled routes do not match durable applied state.",
            innerException)
    {
        ManifestIdentity = manifestIdentity;
        ProviderName = providerName;
    }

    public string ManifestIdentity { get; }

    public string ProviderName { get; }

    public string Code => DiagnosticCode;
}
