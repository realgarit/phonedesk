using System.Diagnostics;
using PhoneDesk.Services;
using PhoneDesk.Topology;

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

    private static async Task ImportStubModuleAsync(PowerShellContextService service)
    {
        var assemblyPath = typeof(InvokePhoneDeskSuccessCommand).Assembly.Location.Replace("'", "''", StringComparison.Ordinal);
        var result = await service.ExecuteCommandWithDetailsAsync(
            $"Import-Module -Name '{assemblyPath}' -Force -ErrorAction Stop",
            null,
            null,
            default);

        Assert.False(result.HadErrors, result.Output);
        Assert.Empty(result.Errors);
    }

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

    [Fact]
    public async Task BinaryStubCmdletOutputFlowsThroughProductionParsers()
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);

        var execution = await service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskSuccess", null, null, default);
        var operation = PowerShellOperationResultMapper.Map(execution, correlationId: "integration-success");
        var topology = new TenantTopologyAssembler().Assemble(execution.Output, DateTimeOffset.UnixEpoch);

        Assert.True(operation.IsSuccess);
        Assert.True(operation.HasSuccessMarker);
        Assert.Equal("integration-success", operation.CorrelationId);
        Assert.Contains("INFO: stub information", execution.Output);
        Assert.Contains("WARNING: stub warning", execution.Output);

        var resourceAccount = Assert.Single(topology.ResourceAccounts);
        Assert.Equal("ra-1", resourceAccount.ObjectId);
        Assert.Equal("+41440000000", resourceAccount.PhoneNumber);

        var callQueue = Assert.Single(topology.CallQueues);
        Assert.Equal("cq-1", callQueue.Identity);
        Assert.Equal(new[] { "agent-1" }, callQueue.AgentObjectIds);

        var group = Assert.Single(topology.Groups);
        Assert.Equal("group-1", group.Id);
    }

    [Theory]
    [InlineData("Invoke-PhoneDeskError", "stub error")]
    [InlineData("Invoke-PhoneDeskThrow", "stub exception")]
    public async Task StubCmdletErrorsBecomeStructuredHandledFailures(string command, string expectedMessage)
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);

        var execution = await service.ExecuteCommandWithDetailsAsync(command, null, null, default);
        var operation = PowerShellOperationResultMapper.Map(execution);

        Assert.True(execution.HadErrors);
        Assert.NotEmpty(execution.Errors);
        Assert.Contains(execution.Errors, error =>
            error.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase) ||
            error.RawText.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
        Assert.False(operation.IsSuccess);
        Assert.True(operation.HasErrorMarker);
        Assert.True(operation.ShouldReportError);
    }

    [Fact]
    public async Task MalformedRowsAreIgnoredWhileValidRowsSurvive()
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);

        var execution = await service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskMalformed", null, null, default);
        var topology = new TenantTopologyAssembler().Assemble(execution.Output, DateTimeOffset.UnixEpoch);

        Assert.False(execution.HadErrors);
        Assert.Empty(topology.ResourceAccounts);
        Assert.Empty(topology.CallQueues);
        var group = Assert.Single(topology.Groups);
        Assert.Equal("group-valid", group.Id);
    }

    [Fact]
    public async Task EmptySuccessfulOutputRemainsAValidEmptyRead()
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);

        var execution = await service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskEmpty", null, null, default);
        var operation = PowerShellOperationResultMapper.Map(execution);
        var topology = new TenantTopologyAssembler().Assemble(execution.Output, DateTimeOffset.UnixEpoch);

        Assert.False(execution.HadErrors);
        Assert.Equal(string.Empty, execution.Output);
        Assert.True(operation.IsSuccess);
        Assert.Empty(topology.ResourceAccounts);
        Assert.Empty(topology.AutoAttendants);
        Assert.Empty(topology.CallQueues);
        Assert.Empty(topology.Groups);
    }

    [Fact]
    public async Task ErrorOnlyOutputIsAHandledFailureWithoutNormalRows()
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);

        var execution = await service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskError", null, null, default);
        var operation = PowerShellOperationResultMapper.Map(execution);
        var topology = new TenantTopologyAssembler().Assemble(execution.Output, DateTimeOffset.UnixEpoch);

        Assert.True(execution.HadErrors);
        Assert.False(operation.IsSuccess);
        Assert.Empty(topology.ResourceAccounts);
        Assert.Empty(topology.AutoAttendants);
        Assert.Empty(topology.CallQueues);
        Assert.Empty(topology.Groups);
    }

    [Theory]
    [InlineData("Invoke-PhoneDeskSuccess")]
    [InlineData("Invoke-PhoneDeskThrow")]
    public async Task EnvironmentVariablesAreClearedAfterEveryExecution(string command)
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);
        var variableName = $"PHONEDESK_INTEGRATION_{Guid.NewGuid():N}".ToUpperInvariant();

        try
        {
            await service.ExecuteCommandWithDetailsAsync(
                command,
                new Dictionary<string, string> { [variableName] = "sensitive-test-value" },
                null,
                default);

            Assert.Null(Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task StubCmdletsProveConcurrentExecutionsNeverOverlap()
    {
        using var service = CreateService();
        await ImportStubModuleAsync(service);
        ConcurrencyProbe.Reset();

        var first = service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskProbe -Name 'first' -DelayMilliseconds 250", null, null, default);
        var second = service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskProbe -Name 'second' -DelayMilliseconds 250", null, null, default);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.False(result.HadErrors, result.Output));
        Assert.Contains("SUCCESS: first", results[0].Output);
        Assert.Contains("SUCCESS: second", results[1].Output);
        Assert.Equal(1, ConcurrencyProbe.MaxActive);
    }
}
