using Groundwork.Documents.UnitOfWork;
using Groundwork.Documents.Scoping;

namespace Groundwork.Documents.Store;

public interface IDocumentStore : IDocumentSessionFactory
{
    /// <summary>The immutable storage access context bound to this store session.</summary>
    DocumentStoreAccess Access { get; }

    Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default);
    Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default);
    Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default);
}
