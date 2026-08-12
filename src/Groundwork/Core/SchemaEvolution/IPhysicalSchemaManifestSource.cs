using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Provider-neutral deployment entry point discovered by the Groundwork schema tool. Application
/// assemblies expose their manifest and optional host naming policy through this contract; provider
/// selection, provider SDKs, connections, and schema execution remain outside Core.
/// </summary>
public interface IPhysicalSchemaManifestSource
{
    StorageManifest CreateManifest();

    IPhysicalNamePolicy CreateNamePolicy() => PhysicalNamePolicy.Identity;
}

/// <summary>
/// A manifest source that the operator parameterizes at the command line.
/// <para>
/// The tool activates a source parameterlessly, which is enough when an assembly deploys one fixed schema.
/// It is not enough when one assembly can deploy several — an application that splits its schema across
/// databases has a different manifest per database, and which one to apply is an operator's choice per
/// invocation, not a property of the type. Such a source cannot be selected by type name alone.
/// </para>
/// <para>
/// The options are opaque here on purpose. Groundwork knows nothing about what an application is
/// partitioning by, and gains nothing by learning: it forwards <c>--manifest-option</c> values verbatim and
/// leaves the vocabulary to the source. Passing them through the environment instead would work, but it
/// hides a required input from <c>--help</c> and from the command that records what was deployed.
/// </para>
/// </summary>
public interface IConfigurablePhysicalSchemaManifestSource : IPhysicalSchemaManifestSource
{
    /// <summary>
    /// Applies the operator-supplied options, before any manifest is created.
    /// <para>
    /// Throw when the options do not select a deployable schema. A source that cannot tell which schema was
    /// asked for must refuse rather than fall back to a default, because the fallback is applied silently
    /// and the operator finds out from the database.
    /// </para>
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> options);
}
