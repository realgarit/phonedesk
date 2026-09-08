using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using PhoneDesk.Audit;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.Topology;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class DashboardViewModelTests
    {
        private const string Sample =
            "TOPRA: RA CQ One|racq-one@contoso.com|ra-obj-1|+41 21 000|CallQueue|True\n" +
            "TOPRA: RA AA One|raaa-one@contoso.com|ra-obj-2|+41 21 111|AutoAttendant|True\n" +
            "TOPRA: Disabled RA|racq-dis@contoso.com|ra-obj-4|+41 21 999|CallQueue|False\n" +
            "TOPAA: Main AA|aa-id-1|en-US|W. Europe Standard Time|ra-obj-2|sched-1|ra-obj-1\n" +
            "TOPCQ: Support CQ|cq-id-1|Attendant|30|agent-1|grp-1|ra-obj-1\n" +
            "TOPGRP: Support Group|grp-1|support|Support team\n";

        private static DashboardViewModel Create(
            ViewModelTestHarness harness,
            ITenantTopologyCache cache,
            Mock<IAuditLog>? auditLog = null)
        {
            harness.SessionManager.SetupGet(manager => manager.TenantId).Returns("tenant-1");
            return new(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                new TenantTopologyAssembler(),
                cache,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                auditLog?.Object);
        }

        [Fact]
        public async Task Refresh_PopulatesCollectionsAndCache()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var cache = new TenantTopologyCache();
            var vm = Create(harness, cache);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(3, vm.ResourceAccounts.Count);
            Assert.Single(vm.AutoAttendants);
            Assert.Single(vm.CallQueues);
            Assert.Single(vm.Groups);
            Assert.True(vm.HasData);
            Assert.True(cache.HasValue);
            Assert.NotNull(vm.LastRefreshedUtc);
        }

        [Fact]
        public async Task Refresh_DetectsAndSurfacesOrphans()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var vm = Create(harness, new TenantTopologyCache());

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.True(vm.HasOrphans);
            // Disabled RA with a phone number is both unassociated-safe (it IS referenced by the CQ? no)
            Assert.Contains(vm.Orphans, o => o.Kind == OrphanKind.PhoneNumberOnDisabledAccount);
        }

        [Fact]
        public void Constructor_RestoresFromCache_WithoutQuerying()
        {
            var cache = new TenantTopologyCache();
            cache.Set("tenant-1", new TenantTopologyAssembler().Assemble(Sample, DateTimeOffset.UtcNow));

            var harness = new ViewModelTestHarness();
            var vm = Create(harness, cache);

            // Populated purely from cache in the constructor — no PowerShell execution.
            Assert.True(vm.HasData);
            Assert.Single(vm.CallQueues);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<System.Collections.Generic.Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public void Constructor_DoesNotRestoreTopologyFromAnotherTenant()
        {
            var cache = new TenantTopologyCache();
            cache.Set("tenant-2", new TenantTopologyAssembler().Assemble(Sample, DateTimeOffset.UtcNow));
            var harness = new ViewModelTestHarness();

            var vm = Create(harness, cache);

            Assert.False(vm.HasData);
            Assert.Empty(vm.CallQueues);
        }

        [Fact]
        public async Task Load_UsesCache_WhenPresent()
        {
            var cache = new TenantTopologyCache();
            cache.Set("tenant-1", new TenantTopologyAssembler().Assemble(Sample, DateTimeOffset.UtcNow));
            var harness = new ViewModelTestHarness();
            var vm = Create(harness, cache);

            await vm.LoadCommand.ExecuteAsync(null);

            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<System.Collections.Generic.Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Load_Queries_WhenCacheEmpty()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var vm = Create(harness, new TenantTopologyCache());

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.HasData);
        }

        [Fact]
        public async Task Search_FiltersAcrossObjectTypes()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var vm = Create(harness, new TenantTopologyCache());
            await vm.RefreshCommand.ExecuteAsync(null);

            vm.SearchText = "raaa-one";
            Assert.Single(vm.ResourceAccountsView);
            Assert.Equal("ra-obj-2", vm.ResourceAccountsView[0].ObjectId);

            vm.SearchText = "+41 21 999";
            Assert.Single(vm.ResourceAccountsView);
            Assert.Equal("ra-obj-4", vm.ResourceAccountsView[0].ObjectId);

            vm.SearchText = "Support";
            Assert.Single(vm.CallQueuesView);
            Assert.Single(vm.GroupsView);

            vm.ClearSearchCommand.Execute(null);
            Assert.Equal(3, vm.ResourceAccountsView.Count);
        }

        [Fact]
        public async Task SelectingAutoAttendant_ResolvesRelationships()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var vm = Create(harness, new TenantTopologyCache());
            await vm.RefreshCommand.ExecuteAsync(null);

            vm.SelectedAutoAttendant = vm.AutoAttendants.Single();

            Assert.Single(vm.SelectedAutoAttendantResourceAccounts);
            Assert.Equal("ra-obj-2", vm.SelectedAutoAttendantResourceAccounts[0].ObjectId);
            // Call target ra-obj-1 belongs to Support CQ -> linked queue.
            Assert.Single(vm.SelectedAutoAttendantCallQueues);
            Assert.Equal("cq-id-1", vm.SelectedAutoAttendantCallQueues[0].Identity);
            Assert.Equal(new[] { "sched-1" }, vm.SelectedAutoAttendantHolidayIds);
        }

        [Fact]
        public async Task SelectingCallQueue_ResolvesAgentsAndGroups()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var vm = Create(harness, new TenantTopologyCache());
            await vm.RefreshCommand.ExecuteAsync(null);

            vm.SelectedCallQueue = vm.CallQueues.Single();

            Assert.Single(vm.SelectedCallQueueGroups);
            Assert.Equal("grp-1", vm.SelectedCallQueueGroups[0].Id);
            Assert.Equal(new[] { "agent-1" }, vm.SelectedCallQueueAgentIds);
            Assert.Single(vm.SelectedCallQueueResourceAccounts);
        }

        [Fact]
        public async Task Refresh_IsAuditedAsReadOperation()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(Sample);
            var auditLog = new Mock<IAuditLog>();
            var vm = Create(harness, new TenantTopologyCache(), auditLog);

            await vm.RefreshCommand.ExecuteAsync(null);

            auditLog.Verify(a => a.Append(It.Is<AuditRecord>(r => r.Kind == AuditKind.Read)), Times.AtLeastOnce);
        }
    }
}
