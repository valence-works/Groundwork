namespace Groundwork.MongoDb.Documents;

/// <summary>
/// Stable fail-closed signal raised when a MongoDB document writer cannot maintain the collection
/// aggregate required by the currently activated physical route.
/// </summary>
public sealed class MongoDbCollectionRouteUpgradeRequiredException : InvalidOperationException
{
    public const string DiagnosticCode = "GW-MONGO-ROUTE-001";

    internal MongoDbCollectionRouteUpgradeRequiredException(
        string storageUnit,
        string writerSignature,
        string requiredSignature,
        Exception? innerException = null)
        : base(
            $"{DiagnosticCode}: MongoDB storage unit '{storageUnit}' requires collection route signature " +
            $"'{requiredSignature}', but this writer provides '{writerSignature}'. Upgrade the writer route before retrying.",
            innerException)
    {
        StorageUnit = storageUnit;
        WriterSignature = writerSignature;
        RequiredSignature = requiredSignature;
    }

    public string StorageUnit { get; }
    public string WriterSignature { get; }
    public string RequiredSignature { get; }
}
