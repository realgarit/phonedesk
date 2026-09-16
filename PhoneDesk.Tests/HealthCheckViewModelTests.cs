using Moq;
using PhoneDesk.HealthChecks;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.Topology;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests;

public sealed class HealthCheckViewModelTests
{
    [Fact]
    public async Task RunRequiresCurrentTenantTopologyBeforeExecutingPowerShell()
    {
        var harness = new ViewModelTestHarness();
        harness.SessionManager.SetupGet(manager => manager.TenantId).Returns("tenant-1");
        var vm = Create(harness, new TenantTopologyCache());

        await vm.RunHealthCheckCommand.ExecuteAsync(null);

        Assert.Contains("Dashboard", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        harness.PowerShellContextService.Verify(
            service => service.ExecuteCommandWithDetailsAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunEvaluatesEnabledRulesCachesResultsAndUsesOnlyReadCommand()
    {
        var harness = CreateConnectedHarness();
        var topologyCache = new TenantTopologyCache();
        topologyCache.Set("tenant-1", CreateHealthyTopology());
        SetupHealthOutput(harness, TenantHealthEnrichmentParserTests.CompleteOutput);
        var resultCache = new TenantHealthCheckCache();
        var vm = Create(harness, topologyCache, healthCheckCache: resultCache);

        await vm.RunHealthCheckCommand.ExecuteAsync(null);

        Assert.True(vm.HasRun);
        var finding = Assert.Single(vm.ActiveFindings);
        Assert.Equal(TenantHealthRuleCatalog.UnassignedServiceNumber, finding.Finding.RuleId);
        Assert.NotNull(resultCache.Current);
        Assert.Single(resultCache.Current.ActiveFindings);
        harness.PowerShellContextService.Verify(
            service => service.ExecuteCommandWithDetailsAsync(
                "Get-TenantHealth", null,
                It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SuppressionAndRuleTogglesPersistAndReevaluateWithoutAnotherTenantQuery()
    {
        var harness = CreateConnectedHarness();
        var topologyCache = new TenantTopologyCache();
        topologyCache.Set("tenant-1", CreateHealthyTopology());
        SetupHealthOutput(harness, TenantHealthEnrichmentParserTests.CompleteOutput);
        var preferences = new Mock<ITenantHealthPreferencesStore>();
        preferences.Setup(store => store.Load("tenant-1")).Returns(TenantHealthPreferences.Default);
        preferences.Setup(store => store.Save("tenant-1", It.IsAny<TenantHealthPreferences>())).Returns(true);
        var vm = Create(harness, topologyCache, preferences.Object);
        await vm.RunHealthCheckCommand.ExecuteAsync(null);
        var finding = Assert.Single(vm.ActiveFindings);

        vm.SuppressFindingCommand.Execute(finding);

        Assert.Empty(vm.ActiveFindings);
        Assert.Single(vm.SuppressedFindings);
        preferences.Verify(store => store.Save(
            "tenant-1",
            It.Is<TenantHealthPreferences>(value =>
                value.SuppressedFindingIds.Contains(finding.Finding.FindingId))), Times.Once);

        var serviceNumberRule = vm.Rules.Single(rule =>
            rule.RuleId == TenantHealthRuleCatalog.UnassignedServiceNumber);
        serviceNumberRule.IsEnabled = false;

        Assert.Empty(vm.ActiveFindings);
        Assert.Empty(vm.SuppressedFindings);
        preferences.Verify(store => store.Save(
            "tenant-1",
            It.Is<TenantHealthPreferences>(value =>
                value.DisabledRuleIds.Contains(TenantHealthRuleCatalog.UnassignedServiceNumber))), Times.Once);
        harness.PowerShellContextService.Verify(
            service => service.ExecuteCommandWithDetailsAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SupportedFindingDeepLinksToEditPage()
    {
        var harness = CreateConnectedHarness();
        var topology = new TenantTopology(
            Array.Empty<TopologyAutoAttendant>(),
            new[]
            {
                new TopologyCallQueue(
                    "Empty queue", "cq-1", "LongestIdle", 20,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            },
            Array.Empty<TopologyResourceAccount>(),
            Array.Empty<TopologyGroup>(),
            Array.Empty<OrphanFinding>(),
            DateTimeOffset.UtcNow);
        var topologyCache = new TenantTopologyCache();
        topologyCache.Set("tenant-1", topology);
        SetupHealthOutput(harness, EmptyHealthOutput);
        var vm = Create(harness, topologyCache);
        await vm.RunHealthCheckCommand.ExecuteAsync(null);
        var finding = Assert.Single(vm.ActiveFindings);

        vm.OpenFindingCommand.Execute(finding);

        harness.NavigationService.Verify(
            service => service.NavigateTo(ConstantsService.Pages.CallQueues),
            Times.Once);
    }

    [Fact]
    public async Task VisibleFindingTextRefreshesWhenLanguageChanges()
    {
        var harness = CreateConnectedHarness();
        var topologyCache = new TenantTopologyCache();
        topologyCache.Set("tenant-1", CreateHealthyTopology());
        SetupHealthOutput(harness, TenantHealthEnrichmentParserTests.CompleteOutput);
        var vm = Create(harness, topologyCache);
        await vm.RunHealthCheckCommand.ExecuteAsync(null);
        var english = Assert.Single(vm.ActiveFindings).Explanation;

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        var german = Assert.Single(vm.ActiveFindings).Explanation;
        Assert.NotEqual(english, german);
        Assert.Contains("Rufnummer", german, StringComparison.OrdinalIgnoreCase);
    }

    private static ViewModelTestHarness CreateConnectedHarness()
    {
        var harness = new ViewModelTestHarness();
        harness.SessionManager.SetupGet(manager => manager.TenantId).Returns("tenant-1");
        return harness;
    }

    private static HealthCheckViewModel Create(
        ViewModelTestHarness harness,
        ITenantTopologyCache topologyCache,
        ITenantHealthPreferencesStore? preferencesStore = null,
        ITenantHealthCheckCache? healthCheckCache = null)
    {
        var preferences = preferencesStore ?? Mock.Of<ITenantHealthPreferencesStore>(store =>
            store.Load(It.IsAny<string>()) == TenantHealthPreferences.Default &&
            store.Save(It.IsAny<string>(), It.IsAny<TenantHealthPreferences>()) == true);
        return new HealthCheckViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            topologyCache,
            new TenantHealthEvaluationService(),
            new TenantHealthEnrichmentParser(),
            Mock.Of<ITenantHealthQueryBuilder>(builder =>
                builder.GetTenantHealthEnrichmentCommand() == "Get-TenantHealth"),
            preferences,
            healthCheckCache ?? new TenantHealthCheckCache(),
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            translationService: harness.TranslationService);
    }

    private static void SetupHealthOutput(ViewModelTestHarness harness, string output)
    {
        harness.PowerShellContextService
            .Setup(service => service.ExecuteCommandWithDetailsAsync(
                "Get-TenantHealth", null,
                It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellExecutionResult { Output = output });
    }

    private static TenantTopology CreateHealthyTopology()
        => new(
            new[]
            {
                new TopologyAutoAttendant(
                    "Reception", "aa-1", "de-DE", "Europe/Zurich",
                    new[] { "ra-1" }, Array.Empty<string>(), Array.Empty<string>()),
            },
            new[]
            {
                new TopologyCallQueue(
                    "Support", "cq-1", "LongestIdle", 20,
                    new[] { "agent-1" }, Array.Empty<string>(), Array.Empty<string>()),
            },
            new[]
            {
                new TopologyResourceAccount(
                    "Reception RA", "reception@contoso.example", "ra-1", null,
                    ResourceAccountKind.AutoAttendant, true),
            },
            Array.Empty<TopologyGroup>(),
            Array.Empty<OrphanFinding>(),
            DateTimeOffset.UtcNow);

    private const string EmptyHealthOutput =
        "HEALTH_RA_START\nHEALTH_RA_END\n" +
        "HEALTH_AA_START\nHEALTH_AA_END\n" +
        "HEALTH_PHONE_START\nHEALTH_PHONE_END\n" +
        "SUCCESS: Tenant health enrichment retrieved";
}
