using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.Relational.PhysicalStorage;

internal static class RelationalPhysicalSchemaLockResource
{
    public static string For(PhysicalSchemaTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(target.ToString()));
        return $"groundwork:physical:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
