using System.Diagnostics;
using PhoneDesk.Services;

namespace PhoneDesk.IntegrationTests;

public sealed class PowerShellContextServiceIntegrationTests
{
    private sealed class CapturingProgress : IProgress<PowerShellProgress>
    {
        private readonly object _gate = new();

        public List<PowerShellProgress> Reports { get; } = new();

        public void Report(PowerShellProgress value)
        {
            lock (_gate)
            {
                Reports.Add(value);
            }
        }
    }

    private static PowerShellContextService CreateService()
        => new(new TestLoggingService());

    [Fact]
    public async Task CancellationStopsLongRunningCommandAndLeavesRunspaceReusable()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();

        var running = service.ExecuteCommandWithDetailsAsync(
            "Start-Sleep -Seconds 30; 'should-not-reach'", null, null, cts.Token);

        await Task.Delay(500);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Cancellation took {stopwatch.Elapsed.TotalSeconds:F1}s; it should interrupt the pipeline promptly.");

        var followUp = await service.ExecuteCommandWithDetailsAsync("'still-alive'", null, null, default);
        Assert.Contains("still-alive", followUp.Output);
        Assert.False(followUp.HadErrors);
    }

    [Fact]
    public async Task ProgressRecordsAreForwardedToTheSuppliedProgressSink()
    {
        using var service = CreateService();
        var progress = new CapturingProgress();

        var result = await service.ExecuteCommandWithDetailsAsync(
            "Write-Progress -Activity 'Provisioning' -Status 'Working' -PercentComplete 42; 'done'",
            null,
            progress,
            default);

        Assert.Contains("done", result.Output);
        Assert.Contains(progress.Reports, report =>
            report.Activity == "Provisioning" &&
            report.PercentComplete == 42 &&
            !report.IsIndeterminate);
    }

    [Fact]
    public async Task CommandWithoutPercentIsReportedAsIndeterminate()
    {
        using var service = CreateService();
        var progress = new CapturingProgress();

        await service.ExecuteCommandWithDetailsAsync(
            "Write-Progress -Activity 'Scanning' -Status 'Please wait'; 'ok'",
            null,
            progress,
            default);

        Assert.Contains(progress.Reports, report => report.Activity == "Scanning" && report.IsIndeterminate);
    }

    [Fact]
    public async Task ConcurrentExecutionsAreSerializedWithoutDeadlock()
    {
        using var service = CreateService();

        var first = service.ExecuteCommandWithDetailsAsync(
            "'A-start'; Start-Sleep -Milliseconds 400; 'A-end'", null, null, default);
        var second = service.ExecuteCommandWithDetailsAsync(
            "'B-start'; 'B-end'", null, null, default);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("A-start", results[0].Output);
        Assert.Contains("A-end", results[0].Output);
        Assert.DoesNotContain("B-start", results[0].Output);

        Assert.Contains("B-start", results[1].Output);
        Assert.DoesNotContain("A-start", results[1].Output);
    }
}
