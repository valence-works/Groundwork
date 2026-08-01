using System.Diagnostics;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkSubprocessCoordinatorTests
{
    [Fact]
    public void Confirmed_regression_worker_exit_is_not_accepted_as_success()
    {
        var response = new BenchmarkWorkerResponse(
            BenchmarkRunProtocol.ProtocolVersion,
            "group",
            1,
            BenchmarkExecutionRole.Measured,
            Succeeded: true,
            RunDirectory: "run",
            ConsumerEvidence: "evidence",
            FailureType: null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkSubprocessCoordinator.EnsureWorkerSucceeded(1, 2, response));

        Assert.Contains("exit code 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_terminates_and_awaits_the_worker_process_tree()
    {
        using var process = Process.Start(LongRunningProcess()) ??
                            throw new InvalidOperationException("Unable to start the cancellation test process.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try
        {
            var wait = BenchmarkSubprocessCoordinator.WaitForExitOrTerminateAsync(
                process,
                cancellation.Token);
            // A watchdog against the wait hanging, not a latency budget: wide enough that a contended runner
            // cannot trip it, and still well inside the child's own 30-second lifetime so a win here means
            // the coordinator terminated the child rather than the child outliving the test.
            var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.Same(wait, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static ProcessStartInfo LongRunningProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            windows.ArgumentList.Add("/c");
            windows.ArgumentList.Add("ping 127.0.0.1 -n 30 > nul");
            return windows;
        }

        var unix = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false
        };
        unix.ArgumentList.Add("-c");
        unix.ArgumentList.Add("sleep 30");
        return unix;
    }
}
