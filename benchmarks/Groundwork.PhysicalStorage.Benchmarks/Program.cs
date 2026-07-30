namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("worker", StringComparison.OrdinalIgnoreCase))
            return await RunWorkerAsync(args);
        if (args.Length > 0 && args[0].Equals("recovery-worker", StringComparison.OrdinalIgnoreCase))
            return await RunRecoveryWorkerAsync(args, verify: false);
        if (args.Length > 0 && args[0].Equals("recovery-verify", StringComparison.OrdinalIgnoreCase))
            return await RunRecoveryWorkerAsync(args, verify: true);
        if (args.Length > 0 && args[0].Equals("recovery-proof", StringComparison.OrdinalIgnoreCase))
            return await RunRecoveryProofAsync(args);
        if (args.Length > 0 && args[0].Equals("recovery-evidence-verify", StringComparison.OrdinalIgnoreCase))
            return await RunRecoveryEvidenceVerifyAsync(args);
        try
        {
            if (args.Length > 0 && args[0].Equals("verify-scheduled-group", StringComparison.OrdinalIgnoreCase))
                return await VerifyScheduledGroupAsync(args);
            var command = BenchmarkCommandLine.Parse(args, FindRepositoryRoot(Environment.CurrentDirectory));
            if (command.ShowHelp)
            {
                Console.WriteLine(BenchmarkCommandLine.Help);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            var result = await new BenchmarkSubprocessCoordinator(Console.WriteLine)
                .RunAsync(command.Request!, cancellation.Token);
            Console.WriteLine($"Benchmark artifact group: {result.RunDirectory}");
            Console.WriteLine($"Independent workers: {result.WorkerCount}");
            return result.ConfirmedRegression ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark run cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> VerifyScheduledGroupAsync(IReadOnlyList<string> args)
    {
        string? root = null;
        string? expectedGitCommit = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
                throw new ArgumentException($"Option '{option}' requires a value.");
            switch (option)
            {
                case "--root":
                    if (root is not null)
                        throw new ArgumentException("Option '--root' may only be supplied once.");
                    root = args[++index];
                    break;
                case "--expected-git-commit":
                    if (expectedGitCommit is not null)
                        throw new ArgumentException("Option '--expected-git-commit' may only be supplied once.");
                    expectedGitCommit = args[++index];
                    break;
                default:
                    throw new ArgumentException($"Unknown scheduled-group verification option '{option}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(expectedGitCommit))
        {
            throw new ArgumentException(
                "Usage: verify-scheduled-group --root <group-root> --expected-git-commit <sha>.");
        }

        var summary = await BenchmarkScheduledGroupVerifier.VerifyAsync(
            root,
            expectedGitCommit,
            CancellationToken.None);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary, BenchmarkJson.CompactOptions));
        return 0;
    }

    private static async Task<int> RunWorkerAsync(IReadOnlyList<string> args)
    {
        string? requestPath = null;
        string? responsePath = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
                return 1;
            switch (option)
            {
                case "--request":
                    requestPath = args[++index];
                    break;
                case "--response":
                    responsePath = args[++index];
                    break;
                default:
                    return 1;
            }
        }
        if (requestPath is null || responsePath is null)
            return 1;
        var invocation = await BenchmarkSubprocessCoordinator.ReadAsync<BenchmarkWorkerInvocation>(
            requestPath,
            CancellationToken.None);
        return await BenchmarkSubprocessCoordinator.RunWorkerAsync(
            invocation,
            responsePath,
            CancellationToken.None,
            BenchmarkSubprocessCoordinator.DigestFile(requestPath));
    }

    private static async Task<int> RunRecoveryWorkerAsync(IReadOnlyList<string> args, bool verify)
    {
        if (args.Count != 3 || args[1] != "--request" || string.IsNullOrWhiteSpace(args[2]))
            return 1;
        try
        {
            if (verify)
            {
                var request = await RecoveryProtocol.ReadAsync<RecoveryVerificationRequest>(args[2], CancellationToken.None);
                await SqliteRecoveryWorker.VerifyAsync(request, CancellationToken.None);
            }
            else
            {
                var request = await RecoveryProtocol.ReadAsync<RecoveryWorkerRequest>(args[2], CancellationToken.None);
                await SqliteRecoveryWorker.RunMutationAsync(request, CancellationToken.None);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RunRecoveryProofAsync(IReadOnlyList<string> args)
    {
        try
        {
            var command = RecoveryProofCommandLine.Parse(args);
            var result = await SqliteProcessFailureRecovery.RunAsync(
                command.StorageForm,
                Path.Combine(Path.GetTempPath(), "groundwork-recovery-proof"),
                command.FailurePoint,
                CancellationToken.None,
                command.Bound,
                command.OutputPath);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Evidence, BenchmarkJson.CompactOptions));
            Console.WriteLine($"Recovery evidence file SHA-256: {result.EvidenceFileSha256}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RunRecoveryEvidenceVerifyAsync(IReadOnlyList<string> args)
    {
        try
        {
            var command = RecoveryEvidenceVerifyCommandLine.Parse(args);
            await RecoveryProtocol.VerifyRetainedAsync(
                command.EvidencePath,
                command.ExpectedEvidenceSha256,
                CancellationToken.None);
            Console.WriteLine("Recovery evidence verified against the caller-provided file SHA-256.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Groundwork.slnx was not found in the current directory hierarchy.");
    }
}
