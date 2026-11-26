using System.Text.Json;
using Moq;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The Holiday Manager page: it shows what the tenant holds, checks a change without sending
    /// anything, runs a change only when the tenant still is what the check showed, and reports the
    /// result from a new read of the tenant.
    /// </summary>
    public class HolidayManagerViewModelTests
    {
        private const string AaId = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string Y2026 = "11111111-0000-0000-0000-000000000001";
        private const string Y2027 = "22222222-0000-0000-0000-000000000002";
        private const string Y2020 = "33333333-0000-0000-0000-000000000003";
        private const string Flow2026 = "f1111111-0000-0000-0000-000000000001";
        private const string Flow2020 = "f3333333-0000-0000-0000-000000000003";

        /// <summary>A tenant for the page: every read returns <see cref="State"/>, every other script runs <see cref="OnChange"/>.</summary>
        private sealed class Tenant
        {
            public string State { get; set; } = string.Empty;
            public List<string> Changes { get; } = new();
            public Action<string>? OnChange { get; set; }

            public void Attach(ViewModelTestHarness harness)
                => harness.PowerShellContextService
                    .Setup(p => p.ExecuteCommandWithDetailsAsync(
                        It.IsAny<string>(),
                        It.IsAny<Dictionary<string, string>?>(),
                        It.IsAny<IProgress<PowerShellProgress>?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                    {
                        if (script.Contains("PDHOLIDAY_JSON", StringComparison.Ordinal))
                        {
                            return new PowerShellExecutionResult { Output = State };
                        }

                        Changes.Add(script);
                        OnChange?.Invoke(script);
                        return new PowerShellExecutionResult { Output = "SUCCESS: done" };
                    });
        }

        private sealed class BackupStore : IHolidayBackupStore
        {
            public List<HolidayBackupDocument> Written { get; } = new();
            public bool Fails { get; set; }

            public string Write(HolidayBackupDocument document)
            {
                if (Fails)
                {
                    throw new IOException("The disk is full.");
                }

                Written.Add(document);
                return "backup-1.json";
            }
        }

        private static HolidayManagerViewModel CreateViewModel(ViewModelTestHarness harness, IHolidayBackupStore? backups = null)
            => new HolidayManagerViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                translationService: harness.TranslationService,
                timeProvider: new FixedClock(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero)),
                backupStore: backups);

        private sealed class FixedClock : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedClock(DateTimeOffset now)
            {
                _now = now;
            }

            public override DateTimeOffset GetUtcNow() => _now;

            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }

        [Fact]
        public async Task Load_ShowsSchedulesAndTheHolidaysOfTheAutoAttendant()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.IsLoaded);
            Assert.Equal(new[] { "Feiertage 2020", "Feiertage 2026", "Feiertage 2027" }, vm.Schedules.Select(row => row.Name));
            Assert.True(vm.Schedules[0].IsExpired);
            Assert.Equal("Main AA", vm.Schedules[1].UsedBy);
            Assert.Equal("Main AA", vm.SelectedAutoAttendant!.Name);
            Assert.Equal(2, vm.Holidays.Count);
            Assert.Contains("TransferCallToTarget", vm.Holidays[1].CallHandling);
            Assert.Contains("2 of 21", vm.AttendantSummary);
            Assert.Empty(tenant.Changes);
        }

        [Fact]
        public async Task EditDates_ChecksFirstThenSavesAndConfirmsFromANewRead()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedSchedule = vm.Schedules.Single(row => row.Id == Y2027);

            vm.OpenEditDatesCommand.Execute(null);
            Assert.Equal("2027-12-25 00:00 to 2027-12-26 00:00", vm.OperationText);
            Assert.False(vm.CanApplyOperation);

            vm.OperationText = "2027-12-24 12:00 to 2027-12-25 00:00\n2027-12-25";
            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyOperation);
            Assert.Contains("Added: 2027-12-24 12:00 to 2027-12-25 00:00", vm.OperationPreview);
            Assert.Empty(tenant.Changes);

            // An input that changes after the check needs a new check.
            vm.AcceptOverlaps = true;
            Assert.False(vm.CanApplyOperation);
            await vm.CheckOperationCommand.ExecuteAsync(null);

            tenant.OnChange = _ => tenant.State = State(
                new[] { Link(Y2026, Flow2026), Link(Y2020, Flow2020) },
                new object[]
                {
                    Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                    Schedule(Y2020, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00", AaId),
                    new
                    {
                        id = Y2027,
                        name = "Feiertage 2027",
                        type = "Fixed",
                        windows = new[]
                        {
                            new { start = "2027-12-24T12:00:00", end = "2027-12-25T00:00:00" },
                            new { start = "2027-12-25T00:00:00", end = "2027-12-26T00:00:00" }
                        },
                        usedBy = Array.Empty<string>()
                    }
                });
            await vm.ApplyOperationCommand.ExecuteAsync(null);

            Assert.Contains("New-CsOnlineDateTimeRange -Start '24/12/2027 12:00' -End '25/12/2027 0:00'", Assert.Single(tenant.Changes));
            Assert.False(vm.ShowOperationDialog);
            Assert.Equal("Saved. The tenant was read again and shows the change.", vm.StatusMessage);
            Assert.Contains("2027-12-24 12:00", vm.Schedules.Single(row => row.Id == Y2027).Dates);
        }

        [Fact]
        public async Task Apply_RunsNothingWhenTheTenantChangedAfterTheCheck()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedSchedule = vm.Schedules.Single(row => row.Id == Y2020);
            vm.OpenRetireCommand.Execute(null);
            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyOperation);
            Assert.Contains("Links taken off: Main AA", vm.OperationPreview);

            // Someone attaches the schedule to nothing any more: the check no longer is what was approved.
            tenant.State = State(
                new[] { Link(Y2026, Flow2026) },
                new object[]
                {
                    Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                    Schedule(Y2020, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00"),
                    Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")
                });
            await vm.ApplyOperationCommand.ExecuteAsync(null);

            Assert.Empty(tenant.Changes);
            Assert.True(vm.ShowOperationDialog);
            Assert.StartsWith("The tenant changed after the check.", vm.StatusMessage);
        }

        [Fact]
        public async Task Apply_WritesABackupOfWhatTheChangeTouchesAndRunsNothingWithoutIt()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var backups = new BackupStore { Fails = true };
            var vm = CreateViewModel(harness, backups);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedSchedule = vm.Schedules.Single(row => row.Id == Y2020);
            vm.OpenRetireCommand.Execute(null);
            await vm.CheckOperationCommand.ExecuteAsync(null);

            await vm.ApplyOperationCommand.ExecuteAsync(null);
            Assert.Empty(tenant.Changes);
            Assert.StartsWith("Nothing was changed: the backup", vm.StatusMessage);

            backups.Fails = false;
            await vm.ApplyOperationCommand.ExecuteAsync(null);
            Assert.Single(tenant.Changes);
            var document = Assert.Single(backups.Written);
            Assert.Equal("Main AA", Assert.Single(document.AutoAttendants).Name);
            Assert.Contains(document.Schedules, schedule => schedule.Id == Y2020);
            Assert.EndsWith("Backup of the state before the change: backup-1.json", vm.StatusMessage);
        }

        [Fact]
        public async Task NothingIsSentWhileTheConnectionsAreNotTheExpectedOnes()
        {
            // The Readiness Check found the wrong tenant: this page must not read or change it.
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var wrongTenant = new ValidationResult();
            wrongTenant.AddError(ValidationErrorCode.TenantIdentityMismatch, "Expected the tenant 'fabrikam.com'.");
            harness.ValidationService.Setup(v => v.ValidatePrerequisites()).Returns(wrongTenant);
            var vm = CreateViewModel(harness);

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.False(vm.IsLoaded);
            Assert.Empty(tenant.Changes);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Retire_RefusesAScheduleWithAFutureDateUntilTheOperatorAllowsIt()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedSchedule = vm.Schedules.Single(row => row.Id == Y2026);
            vm.OpenRetireCommand.Execute(null);

            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.False(vm.CanApplyOperation);
            Assert.Contains("Cannot be saved:", vm.OperationPreview);

            vm.AllowNotExpired = true;
            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyOperation);
        }

        [Fact]
        public async Task Attach_CopiesTheCallHandlingAndReportsAnAutoAttendantTheTenantDoesNotShow()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedHoliday = vm.Holidays.Single(row => row.ScheduleId == Y2026);

            vm.OpenAttachCommand.Execute(null);
            Assert.Equal(Y2027, Assert.Single(vm.TargetChoices).Id);
            Assert.True(Assert.Single(vm.AttendantChoices).IsSelected);

            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyOperation);
            Assert.Contains("Main AA: will be attached.", vm.OperationPreview);

            // The script reports success, but the tenant does not show the schedule on the auto attendant.
            await vm.ApplyOperationCommand.ExecuteAsync(null);
            Assert.Contains(Y2027, Assert.Single(tenant.Changes));
            Assert.Equal("The tenant does not show the whole change: Main AA: not attached", vm.StatusMessage);
        }

        [Fact]
        public async Task ReplaceGreeting_NeedsExactlyOneOfTextAndAudioFile()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { State = Before() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedHoliday = vm.Holidays.Single(row => row.ScheduleId == Y2026);

            vm.OpenReplaceGreetingCommand.Execute(null);
            Assert.Equal("Wir sind geschlossen.", vm.OperationText);

            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.False(vm.CanApplyOperation);
            Assert.Contains("There is nothing to save.", vm.OperationPreview);

            vm.OperationSecondText = "a0a0a0a0-0000-0000-0000-00000000000a";
            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.False(vm.CanApplyOperation);
            Assert.Contains("not both and not neither", vm.OperationPreview);

            vm.OperationText = string.Empty;
            await vm.CheckOperationCommand.ExecuteAsync(null);
            Assert.True(vm.CanApplyOperation);
        }

        private static string Before()
            => State(
                new[] { Link(Y2026, Flow2026), Link(Y2020, Flow2020) },
                new object[]
                {
                    Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                    Schedule(Y2020, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00", AaId),
                    Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")
                });

        private static string State(IEnumerable<(string Schedule, string Flow)> links, IEnumerable<object> schedules)
            => HolidayCallHandling.JsonMarker + JsonSerializer.Serialize(new
            {
                autoAttendants = new[]
                {
                    new
                    {
                        id = AaId,
                        name = "Main AA",
                        authorizedUsers = Array.Empty<string>(),
                        callFlows = links.Select(link => Flow(link.Flow, link.Flow == Flow2026 ? "Wir sind geschlossen." : "Alt")),
                        associations = links.Select(link => new { type = "Holiday", scheduleId = link.Schedule, callFlowId = link.Flow, enabled = true, priority = 0 })
                    }
                },
                schedules,
                missingAuthorizedUsers = Array.Empty<object>(),
                uncheckedAuthorizedUsers = Array.Empty<object>(),
                disabledAuthorizedUsers = Array.Empty<object>()
            }) + "\nSUCCESS: Holiday call handling retrieved";

        private static (string Schedule, string Flow) Link(string scheduleId, string callFlowId) => (scheduleId, callFlowId);

        private static object Flow(string id, string greeting)
            => new
            {
                id,
                name = "call flow",
                greetings = new[] { new { type = "TextToSpeech", text = greeting, audioFile = (object?)null } },
                menuName = "menu",
                menuPrompts = Array.Empty<object>(),
                options = new[]
                {
                    new
                    {
                        action = "TransferCallToTarget",
                        key = "Automatic",
                        voiceResponses = Array.Empty<string>(),
                        target = new { id = "tel:+41441112233", type = "ExternalPstn", enableTranscription = (bool?)null, suppressSystemPrompt = (bool?)null, callPriority = 0 },
                        prompt = (object?)null
                    }
                },
                forceListenMenu = false,
                dialByName = false
            };

        private static object Schedule(string id, string name, string start, string end, params string[] usedBy)
            => new { id, name, type = "Fixed", windows = new[] { new { start, end } }, usedBy };
    }
}
