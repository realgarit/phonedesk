using Moq;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The run record of the desktop app: Guided Setup writes what it created, a later visit offers
    /// to resume, and the Setup Runs page removes only what the check allowed, in the tenant of the run.
    /// </summary>
    public sealed class SetupRunsTests : IDisposable
    {
        private const string Tenant = "tenant-1";
        private const string GroupId = "11111111-2222-3333-4444-555555555555";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "phonedesk-runs-" + Guid.NewGuid().ToString("N"));
        private readonly ViewModelTestHarness _harness = new();
        private readonly RunJournalService _journal;
        private readonly List<string> _scripts = new();
        private string _identity = string.Empty;

        public SetupRunsTests()
        {
            _harness.SessionManager.SetupGet(session => session.TenantId).Returns(Tenant);
            _harness.SessionManager.SetupGet(session => session.TenantName).Returns("Contoso AG");
            _harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("CREATE GROUP");
            _harness.PowerShellCommandService.Setup(c => c.GetRunIdentityCommand(It.IsAny<IReadOnlyList<RunObject>>())).Returns("READ IDENTITY");
            _harness.PowerShellCommandService.Setup(c => c.GetRemoveM365GroupCommand(It.IsAny<string>(), It.IsAny<string>())).Returns("REMOVE GROUP");
            _harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                {
                    _scripts.Add(script);
                    return new PowerShellExecutionResult
                    {
                        Output = script == "READ IDENTITY" ? _identity : RunJournal.StepMarker + "0" + Environment.NewLine + "SUCCESS: removed"
                    };
                });
            _journal = new RunJournalService(new FileRunJournalStore(_directory), _harness.PowerShellCommandService.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static string Identity(params string[] ids)
            => RunJournal.IdentityMarker + System.Text.Json.JsonSerializer.Serialize(new[] { new { index = 0, ids } });

        private RunRecord RunWithGroup(string tenant)
        {
            var record = _journal.Begin(RunKind.GuidedSetup, "desktop", tenant, "Contoso AG", "contoso - reception");
            record.Objects.Add(new RunObject { Type = RunObjectType.M365Group, Name = "ttgrp-contoso-reception", ObjectId = GroupId, Step = 1 });
            record.State = RunState.Failed;
            _journal.Save(record);
            return record;
        }

        private SetupRunsViewModel Page() => new(
            _harness.PowerShellContextService.Object, _harness.PowerShellCommandService.Object, _harness.LoggingService.Object,
            _harness.SessionManager.Object, _harness.NavigationService.Object, _harness.ErrorHandlingService.Object,
            _harness.ValidationService.Object, _harness.SharedStateService.Object, _harness.DialogService.Object,
            translationService: _harness.TranslationService, journal: _journal);

        private WizardViewModel Wizard() => new(
            _harness.PowerShellContextService.Object, _harness.PowerShellCommandService.Object, _harness.LoggingService.Object,
            _harness.SessionManager.Object, _harness.NavigationService.Object, _harness.ErrorHandlingService.Object,
            _harness.ValidationService.Object, _harness.SharedStateService.Object, _harness.DialogService.Object,
            journal: _journal);

        [Fact]
        public async Task Page_RollsBackOnlyAfterACheckThatIsStillTrue()
        {
            var record = RunWithGroup(Tenant);
            var page = Page();
            Assert.Equal(record.Id, page.SelectedRun?.Id);
            Assert.False(page.CanRollBack);

            _identity = Identity(GroupId);
            await page.CheckRollbackCommand.ExecuteAsync(null);
            Assert.True(page.CanRollBack);
            Assert.Contains("ttgrp-contoso-reception", page.Details);

            // The group got a namesake after the check: nothing is removed.
            _identity = Identity(GroupId, "another");
            await page.RollBackCommand.ExecuteAsync(null);
            Assert.DoesNotContain("REMOVE GROUP", string.Join("|", _scripts));
            Assert.True(_journal.Find(record.Id)!.CanRollBack);

            _identity = Identity(GroupId);
            await page.CheckRollbackCommand.ExecuteAsync(null);
            await page.RollBackCommand.ExecuteAsync(null);
            Assert.Contains(_scripts, script => script.Contains("REMOVE GROUP", StringComparison.Ordinal));
            Assert.Equal(RunState.RolledBack, _journal.Find(record.Id)!.State);
            Assert.False(page.CanRollBack);
        }

        [Fact]
        public async Task Page_DoesNotSearchAnotherTenantForTheObjectsOfARun()
        {
            RunWithGroup("another-tenant");
            var page = Page();

            await page.CheckRollbackCommand.ExecuteAsync(null);
            await page.RollBackCommand.ExecuteAsync(null);

            Assert.Empty(_scripts);
            Assert.False(page.CanRollBack);
            Assert.Contains("another tenant", page.StatusMessage);
        }

        [Fact]
        public async Task GuidedSetup_RecordsWhatItCreatedAndOffersTheRunAgainLater()
        {
            var wizard = Wizard();
            await wizard.ExecuteCurrentStepCommand.ExecuteAsync(null);
            wizard.GoToNextStepCommand.Execute(null);
            Assert.Equal(1, wizard.CurrentStep);
            var group = wizard.Variables.M365Group;
            _identity = Identity(GroupId);
            _harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "CREATE GROUP", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = $"SUCCESS: {group} created successfully with ID: {GroupId}" });

            await wizard.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(wizard.Steps[1].IsCompleted);
            var record = Assert.Single(_journal.List());
            Assert.Equal(Tenant, record.TenantId);
            Assert.Equal(GroupId, Assert.Single(record.Objects).ObjectId);
            Assert.True(record.CompletedSteps.ContainsKey(1));
            Assert.True(wizard.CanRollBackRun);

            // The application was closed. The next visit offers the run, and resuming restores the step.
            var later = Wizard();
            Assert.True(later.HasPendingRun);
            Assert.False(later.Steps[1].IsCompleted);
            later.ResumePendingRunCommand.Execute(null);
            Assert.False(later.HasPendingRun);
            Assert.True(later.Steps[1].IsCompleted);
            Assert.True(later.CanRollBackRun);

            // A run of another customer's tenant is not offered.
            _harness.SessionManager.SetupGet(session => session.TenantId).Returns("another-tenant");
            Assert.False(Wizard().HasPendingRun);
        }
    }
}
