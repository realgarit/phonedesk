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
    public async Task NullPipelineOutputDoesNotReplaceTheScriptOutputWithAnError()
    {
        // Some Teams cmdlets write a null value to the pipeline after their work is done.
        using var service = CreateService();

        var result = await service.ExecuteCommandWithDetailsAsync(
            "Write-Host 'SUCCESS: done'; $null; 'after'", null, null, TestContext.Current.CancellationToken);

        Assert.False(result.HadErrors, result.Output);
        Assert.Empty(result.Errors);
        Assert.Contains("SUCCESS: done", result.Output, StringComparison.Ordinal);
        Assert.Contains("after", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachSessionKeepsItsOwnStateAndASwitchBackFindsItAgain()
    {
        // A profile has its own session: what one customer's session holds (its connections) is
        // not seen in another, and is there again after a switch back.
        using var service = CreateService();
        var token = TestContext.Current.CancellationToken;
        await service.ExecuteCommandWithDetailsAsync("$global:pdMark = 'contoso'", null, null, token);

        service.UseSession("fabrikam");
        Assert.Equal("fabrikam", service.SessionKey);
        var other = await service.ExecuteCommandWithDetailsAsync("Write-Host \"mark=[$global:pdMark]\"", null, null, token);
        Assert.Contains("mark=[]", other.Output, StringComparison.Ordinal);
        await service.ExecuteCommandWithDetailsAsync("$global:pdMark = 'fabrikam'", null, null, token);

        service.UseSession(string.Empty);
        var back = await service.ExecuteCommandWithDetailsAsync("Write-Host \"mark=[$global:pdMark]\"", null, null, token);
        Assert.Contains("mark=[contoso]", back.Output, StringComparison.Ordinal);

        // A session that is closed starts empty when it is used again.
        service.CloseSession("fabrikam");
        service.UseSession("fabrikam");
        var reopened = await service.ExecuteCommandWithDetailsAsync("Write-Host \"mark=[$global:pdMark]\"", null, null, token);
        Assert.Contains("mark=[]", reopened.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeCheckedForOneProfileIsNotSentAfterASwitch()
    {
        using var service = CreateService();
        var token = TestContext.Current.CancellationToken;
        using (PhoneDesk.Operations.SessionScope.Expect(service.SessionKey))
        {
            // The profile is switched between the check of the change and its command.
            service.UseSession("fabrikam");
            var refused = await service.ExecuteCommandWithDetailsAsync("$global:pdSent = 'yes'", null, null, token);
            Assert.True(refused.HadErrors);
            Assert.Contains("Nothing was sent", refused.Output, StringComparison.Ordinal);
        }

        // Outside a change the command runs, and the refused one left no trace in either session.
        var check = await service.ExecuteCommandWithDetailsAsync("Write-Host \"sent=[$global:pdSent]\"", null, null, token);
        Assert.Contains("sent=[]", check.Output, StringComparison.Ordinal);
        service.UseSession(string.Empty);
        var first = await service.ExecuteCommandWithDetailsAsync("Write-Host \"sent=[$global:pdSent]\"", null, null, token);
        Assert.Contains("sent=[]", first.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatCannotBeStoppedIsGivenUpWithItsSession()
    {
        // Like a Microsoft Teams sign-in whose window was closed: a .NET wait that the pipeline
        // stop does not interrupt. Giving up the session lets the next command run at once.
        using var service = CreateService();
        var token = TestContext.Current.CancellationToken;
        await service.ExecuteCommandWithDetailsAsync("$global:pdMark = 'before'", null, null, token);
        var stuck = service.ExecuteCommandWithDetailsAsync("[System.Threading.Thread]::Sleep(4000); 'late'", null, null, CancellationToken.None);
        await Task.Delay(300, token);
        Assert.False(stuck.IsCompleted);

        Assert.True(service.AbandonSession());

        var stopwatch = Stopwatch.StartNew();
        var next = await service.ExecuteCommandWithDetailsAsync("Write-Host \"mark=[$global:pdMark]\"; 'fresh'", null, null, token);
        stopwatch.Stop();
        // The old command sleeps four seconds: the new session did not wait for it.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The new session took {stopwatch.Elapsed.TotalSeconds:F1}s.");
        Assert.Contains("fresh", next.Output, StringComparison.Ordinal);
        // The new session is empty: the connections of the old one are gone.
        Assert.Contains("mark=[]", next.Output, StringComparison.Ordinal);
        Assert.False(stuck.IsCompleted);

        // The old command ends some time later; its result reaches nobody who still waits for it,
        // and it must not break the host. It is awaited here only so the test ends after it.
        // The given-up session was stopped, so the old command ends as cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(30), token));
        var after = await service.ExecuteCommandWithDetailsAsync("'still-fine'", null, null, token);
        Assert.Contains("still-fine", after.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsLongRunningCommandAndLeavesRunspaceReusable()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();

        var running = service.ExecuteCommandWithDetailsAsync(
            "Start-Sleep -Seconds 30; 'should-not-reach'", null, null, cts.Token);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Cancellation took {stopwatch.Elapsed.TotalSeconds:F1}s; it should interrupt the pipeline promptly.");

        var followUp = await service.ExecuteCommandWithDetailsAsync("'still-alive'", null, null, TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

        Assert.Contains(progress.Reports, report => report.Activity == "Scanning" && report.IsIndeterminate);
    }

    [Fact]
    public async Task ConcurrentExecutionsAreSerializedWithoutDeadlock()
    {
        using var service = CreateService();

        var first = service.ExecuteCommandWithDetailsAsync(
            "'A-start'; Start-Sleep -Milliseconds 400; 'A-end'", null, null, TestContext.Current.CancellationToken);
        var second = service.ExecuteCommandWithDetailsAsync(
            "'B-start'; 'B-end'", null, null, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
            "Invoke-PhoneDeskSuccess", null, null, TestContext.Current.CancellationToken);
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

        var execution = await service.ExecuteCommandWithDetailsAsync(command, null, null, TestContext.Current.CancellationToken);
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
            "Invoke-PhoneDeskMalformed", null, null, TestContext.Current.CancellationToken);
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
            "Invoke-PhoneDeskEmpty", null, null, TestContext.Current.CancellationToken);
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
            "Invoke-PhoneDeskError", null, null, TestContext.Current.CancellationToken);
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
                TestContext.Current.CancellationToken);

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
            "Invoke-PhoneDeskProbe -Name 'first' -DelayMilliseconds 250", null, null, TestContext.Current.CancellationToken);
        var second = service.ExecuteCommandWithDetailsAsync(
            "Invoke-PhoneDeskProbe -Name 'second' -DelayMilliseconds 250", null, null, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.False(result.HadErrors, result.Output));
        Assert.Contains("SUCCESS: first", results[0].Output);
        Assert.Contains("SUCCESS: second", results[1].Output);
        Assert.Equal(1, ConcurrencyProbe.MaxActive);
    }
}
