using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using PhoneDesk.Audit;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class HistoryViewModelTests
    {
        private static AuditRecord Rec(
            string operation,
            AuditOutcome outcome = AuditOutcome.Success,
            AuditKind kind = AuditKind.Operation,
            string? target = "contoso",
            int minutesAgo = 0)
            => new()
            {
                TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                Operation = operation,
                Target = target,
                Outcome = outcome,
                Kind = kind,
                CorrelationId = Guid.NewGuid().ToString("N")
            };

        private static HistoryViewModel Create(IReadOnlyList<AuditRecord> records, out Mock<IAuditLog> auditLog)
        {
            var harness = new ViewModelTestHarness();
            auditLog = new Mock<IAuditLog>();
            auditLog.Setup(a => a.Read()).Returns(records);
            auditLog.SetupGet(a => a.LogDirectoryPath).Returns("/tmp/audit");

            return new HistoryViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                auditLog.Object);
        }

        [Fact]
        public void Load_ExcludesReadOperationsByDefault()
        {
            var vm = Create(new[]
            {
                Rec("Create Call Queue", kind: AuditKind.Operation),
                Rec("RetrieveCallQueues", kind: AuditKind.Read)
            }, out _);

            Assert.Single(vm.Records);
            Assert.Equal("Create Call Queue", vm.Records[0].Operation);
            Assert.Equal(2, vm.TotalCount);
        }

        [Fact]
        public void ShowReadOperations_IncludesReadEntries()
        {
            var vm = Create(new[]
            {
                Rec("Create Call Queue", kind: AuditKind.Operation),
                Rec("RetrieveCallQueues", kind: AuditKind.Read)
            }, out _);

            vm.ShowReadOperations = true;

            Assert.Equal(2, vm.Records.Count);
        }

        [Fact]
        public void OutcomeFilter_FiltersByOutcome()
        {
            var vm = Create(new[]
            {
                Rec("Op OK", outcome: AuditOutcome.Success),
                Rec("Op Fail", outcome: AuditOutcome.Failure)
            }, out _);

            vm.SelectedOutcomeIndex = 2; // Failure

            Assert.Single(vm.Records);
            Assert.Equal("Op Fail", vm.Records[0].Operation);
        }

        [Fact]
        public void ObjectFilter_MatchesTargetOrOperation_CaseInsensitive()
        {
            var vm = Create(new[]
            {
                Rec("Create Call Queue", target: "contoso-sales"),
                Rec("Create Auto Attendant", target: "fabrikam-support")
            }, out _);

            vm.ObjectFilter = "FABRIKAM";

            Assert.Single(vm.Records);
            Assert.Equal("Create Auto Attendant", vm.Records[0].Operation);
        }

        [Fact]
        public void DateFilter_ExcludesRecordsOutsideRange()
        {
            var vm = Create(new[]
            {
                Rec("Recent", minutesAgo: 0),
                Rec("Old", minutesAgo: 60 * 24 * 5) // 5 days ago
            }, out _);

            vm.FromDate = DateTimeOffset.UtcNow.AddDays(-1);

            Assert.Single(vm.Records);
            Assert.Equal("Recent", vm.Records[0].Operation);
        }

        [Fact]
        public void ClearFilters_ResetsToDefault()
        {
            var vm = Create(new[]
            {
                Rec("Op", kind: AuditKind.Operation),
                Rec("Read", kind: AuditKind.Read)
            }, out _);

            vm.ShowReadOperations = true;
            vm.SelectedOutcomeIndex = 1;
            vm.ObjectFilter = "zzz";

            vm.ClearFiltersCommand.Execute(null);

            Assert.False(vm.ShowReadOperations);
            Assert.Equal(0, vm.SelectedOutcomeIndex);
            Assert.Equal(string.Empty, vm.ObjectFilter);
            Assert.Null(vm.FromDate);
            Assert.Null(vm.ToDate);
            Assert.Single(vm.Records); // read entry excluded again
        }
    }
}
