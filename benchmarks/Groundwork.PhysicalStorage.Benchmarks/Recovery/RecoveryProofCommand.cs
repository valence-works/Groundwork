using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal sealed record RecoveryProofCommand(
    PhysicalStorageForm StorageForm,
    RecoveryFailurePoint FailurePoint,
    string OutputPath,
    TimeSpan Bound);

internal static class RecoveryProofCommandLine
{
    internal static RecoveryProofCommand Parse(IReadOnlyList<string> args)
    {
        PhysicalStorageForm? storageForm = null;
        RecoveryFailurePoint? failurePoint = null;
        string? output = null;
        var timeoutMilliseconds = RecoveryProtocol.TimeoutMilliseconds;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
                throw new ArgumentException($"Option '{option}' requires a value.");
            var value = args[++index];
            switch (option)
            {
                case "--form":
                    if (storageForm is not null)
                        throw new ArgumentException("Option '--form' may only be supplied once.");
                    storageForm = value switch
                    {
                        "shared" => PhysicalStorageForm.SharedDocuments,
                        "dedicated" => PhysicalStorageForm.DedicatedDocumentTable,
                        "entity" => PhysicalStorageForm.PhysicalEntityTable,
                        _ => throw new ArgumentException("Option '--form' requires shared, dedicated, or entity.")
                    };
                    break;
                case "--failure-point":
                    if (failurePoint is not null)
                        throw new ArgumentException("Option '--failure-point' may only be supplied once.");
                    failurePoint = value switch
                    {
                        "pre-commit" => RecoveryFailurePoint.PreCommit,
                        "committed-before-ack" => RecoveryFailurePoint.CommittedBeforeAcknowledgement,
                        _ => throw new ArgumentException("Option '--failure-point' requires pre-commit or committed-before-ack.")
                    };
                    break;
                case "--output":
                    if (output is not null)
                        throw new ArgumentException("Option '--output' may only be supplied once.");
                    output = Path.GetFullPath(value);
                    break;
                case "--timeout-ms":
                    if (!int.TryParse(value, out timeoutMilliseconds) || timeoutMilliseconds < 1)
                        throw new ArgumentException("Option '--timeout-ms' requires a positive integer.");
                    break;
                default:
                    throw new ArgumentException($"Unknown recovery-proof option '{option}'.");
            }
        }
        if (storageForm is null || failurePoint is null || string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException(
                "Usage: recovery-proof --form shared|dedicated|entity --failure-point pre-commit|committed-before-ack --output <evidence.json> [--timeout-ms <positive-int>].");
        }
        return new RecoveryProofCommand(storageForm.Value, failurePoint.Value, output, TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }
}
