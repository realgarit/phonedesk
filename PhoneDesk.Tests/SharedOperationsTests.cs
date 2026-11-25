using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PhoneDesk.Audit;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using Xunit;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The code both heads share (desktop app and MCP server): the same configuration must give the
    /// same scripts, and the command runner must audit and report every attempt the same way.
    /// </summary>
    public class SharedOperationsTests
    {
        private static PowerShellCommandService CreateCommands()
        {
            var sanitizer = new PowerShellSanitizationService();
            return new PowerShellCommandService(
                new CommonScriptBuilder(sanitizer),
                new CallQueueScriptBuilder(sanitizer),
                new AutoAttendantScriptBuilder(sanitizer),
                new HolidayScriptBuilder(sanitizer),
                new ResourceAccountScriptBuilder(sanitizer),
                new DashboardScriptBuilder());
        }

        private static PhoneManagerVariables DesktopVariables()
        {
            var variables = new PhoneManagerVariables
            {
                Customer = "contoso",
                CustomerGroupName = "hauptnummer",
                MsFallbackDomain = "contoso.onmicrosoft.com",
                RaaAnrName = "haupt",
                CustomerLegalName = "Contoso AG",
                LanguageId = "de-DE",
                TimeZoneId = "W. Europe Standard Time",
                UsageLocation = "CH",
                RaaAnr = "+41441234567",
                PhoneNumberType = "DirectRouting",
                HolidayNameSuffix = "2027",
                HolidayGreetingPromptDE = "Wir sind heute nicht erreichbar.",
                CqGreetingType = "TextToSpeech",
                CqGreetingTextToSpeechPrompt = "Willkommen",
                CqOverflowThreshold = 5,
                CqOverflowAction = "Disconnect",
                CqTimeoutThreshold = 120,
                CqTimeoutAction = "Disconnect",
                AaDefaultGreetingType = "TextToSpeech",
                AaDefaultGreetingTextToSpeechPrompt = "Guten Tag",
                AaDefaultAction = "TransferToTarget",
                AaAfterHoursGreetingType = "TextToSpeech",
                AaAfterHoursGreetingTextToSpeechPrompt = "Wir sind geschlossen",
                AaAfterHoursAction = "Disconnect",
                OpeningHours1Start = new TimeSpan(8, 0, 0),
                OpeningHours1End = new TimeSpan(12, 0, 0),
                OpeningHours2Start = new TimeSpan(13, 30, 0),
                OpeningHours2End = new TimeSpan(17, 0, 0),
                UsePerDaySchedule = true
            };
            variables.AaDefaultActionTarget = variables.RacqUPN;
            variables.WeeklySchedule[4].HasSecondRange = true;
            variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2027, 1, 1), new TimeSpan(0, 0, 0), "Neujahr"));
            variables.HolidaySeries.Add(new HolidayEntry(
                new DateTime(2027, 12, 24), new TimeSpan(12, 0, 0), "Weihnachten", new DateTime(2027, 12, 27), new TimeSpan(8, 0, 0)));
            return variables;
        }

        [Fact]
        public void ConfigurationDocument_GivesTheSameScriptsAsTheDesktopModel()
        {
            var desktop = DesktopVariables();
            var headless = new ConfigurationDocumentVariables(PhoneManagerVariablesPortabilityMapper.ToDocument(desktop));
            var factory = new GuidedSetupScriptFactory(CreateCommands());

            for (var step = GuidedSetupScriptFactory.FirstProvisioningStep; step <= GuidedSetupScriptFactory.LastProvisioningStep; step++)
            {
                Assert.Equal(factory.GetProvisioningScript(step, desktop), factory.GetProvisioningScript(step, headless));
            }

            var commands = CreateCommands();
            Assert.Equal(
                commands.GetCreateHolidaySeriesFromEntriesCommand(desktop.HolidayName, desktop.HolidaySeries),
                commands.GetCreateHolidaySeriesFromEntriesCommand(headless.HolidayName, headless.HolidaySeries));
            Assert.Equal(desktop.RaaaUPN, headless.RaaaUPN);
            Assert.Equal(desktop.AaDisplayName, headless.AaDisplayName);
        }

        [Fact]
        public void DefaultConfigurationDocument_MatchesANewDesktopConfiguration()
        {
            var today = new DateOnly(2027, 3, 1);
            var desktop = new PhoneManagerVariables { HolidayDate = today.ToDateTime(TimeOnly.MinValue) };

            var expected = PhoneManagerVariablesPortabilityMapper.ToDocument(desktop);
            var actual = ConfigurationDocumentVariables.CreateDefaultDocument(today);

            var service = new TenantAsCodeService();
            Assert.Equal(service.SerializeConfiguration(expected), service.SerializeConfiguration(actual));
        }

        [Fact]
        public void BulkScript_IsTheSameFromTheDesktopBuilderAndTheSharedBuilder()
        {
            var commands = CreateCommands();
            var sanitizer = new PowerShellSanitizationService();
            var desktopBuilder = new BulkOperationsScriptBuilder(commands, sanitizer);
            var sharedBuilder = new BulkSetupScriptBuilder(commands, sanitizer);
            var csv = desktopBuilder.GenerateCsvTemplate();

            var desktopEntries = desktopBuilder.ParseCsv(csv);
            var sharedEntries = new List<IPhoneManagerVariables>();
            foreach (var entry in desktopEntries)
            {
                sharedEntries.Add(new ConfigurationDocumentVariables(PhoneManagerVariablesPortabilityMapper.ToDocument(entry)));
            }

            Assert.Equal(desktopBuilder.GenerateBulkScript(desktopEntries), sharedBuilder.GenerateBulkScript(sharedEntries));
        }

        [Fact]
        public async Task Runner_AuditsAChangeWithItsTargetAndOutcome()
        {
            var (runner, powerShell, auditLog, _) = CreateRunner();
            powerShell
                .Setup(service => service.ExecuteCommandWithDetailsAsync(
                    "script", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PowerShellExecutionResult { Output = "ERROR: failed", HadErrors = true });

            var outcome = await runner.RunAsync(
                new TenantCommandRequest("script", "CreateCallQueue") { Target = "cq-contoso" },
                TestContext.Current.CancellationToken);

            Assert.Equal(TenantCommandStatus.Completed, outcome.Status);
            Assert.False(outcome.Result.IsSuccess);
            auditLog.Verify(log => log.Append(It.Is<AuditRecord>(record =>
                record.Operation == "CreateCallQueue" &&
                record.Target == "cq-contoso" &&
                record.Outcome == AuditOutcome.Failure &&
                record.Kind == AuditKind.Operation &&
                record.TenantId == "tenant-1")), Times.Once);
        }

        [Fact]
        public async Task Runner_DoesNotRunAnythingAfterTheSessionExpired()
        {
            var (runner, powerShell, auditLog, session) = CreateRunner();
            session.SetupGet(manager => manager.IsSessionExpired).Returns(true);
            session.SetupGet(manager => manager.IsSessionValid).Returns(true);

            var outcome = await runner.RunAsync(
                new TenantCommandRequest("script", "CreateCallQueue"), TestContext.Current.CancellationToken);

            Assert.Equal(TenantCommandStatus.SessionExpired, outcome.Status);
            Assert.Equal(OperationErrorCategory.AuthSession, outcome.Result.Category);
            session.Verify(manager => manager.ResetSession(), Times.Once);
            powerShell.VerifyNoOtherCalls();
            auditLog.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Runner_ReportsCancellationAndAHostFaultWithoutThrowing()
        {
            var (runner, powerShell, auditLog, _) = CreateRunner();
            powerShell
                .Setup(service => service.ExecuteCommandWithDetailsAsync(
                    "cancelled", null, null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
            powerShell
                .Setup(service => service.ExecuteCommandWithDetailsAsync(
                    "faulted", null, null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("runspace broke"));

            var cancelled = await runner.RunAsync(
                new TenantCommandRequest("cancelled", "Read"), TestContext.Current.CancellationToken);
            var faulted = await runner.RunAsync(
                new TenantCommandRequest("faulted", "Read"), TestContext.Current.CancellationToken);

            Assert.Equal(TenantCommandStatus.Cancelled, cancelled.Status);
            Assert.Equal(OperationErrorCategory.Cancelled, cancelled.Result.Category);
            Assert.Equal(TenantCommandStatus.Faulted, faulted.Status);
            Assert.Equal("runspace broke", faulted.Result.ErrorMessage);
            auditLog.Verify(log => log.Append(It.Is<AuditRecord>(record => record.Outcome == AuditOutcome.Cancelled)), Times.Once);
            auditLog.Verify(log => log.Append(It.Is<AuditRecord>(record =>
                record.Outcome == AuditOutcome.Failure && record.ErrorDetail == "runspace broke")), Times.Once);
        }

        [Fact]
        public void OutputParser_ReadsTheMarkerLinesOfTheFrozenScripts()
        {
            var queues = TenantOutputParser.ParseCallQueues("CALLQUEUE: cq-a | id-1 | Attendant | 30\nnoise\nCALLQUEUE: short|row\n");
            var queue = Assert.Single(queues);
            Assert.Equal(new CallQueueRow("cq-a", "id-1", "Attendant", 30), queue);

            Assert.Equal("group-id", TenantOutputParser.ParseM365GroupId("INFO\nM365GROUPID: group-id \n"));
            Assert.Equal(
                "3a1f1e0c-5d1a-4a55-9c7b-0d2f4e6a8b10",
                TenantOutputParser.ParseAudioFileId("AUDIOFILEID: 3a1f1e0c-5d1a-4a55-9c7b-0d2f4e6a8b10"));
            Assert.Null(TenantOutputParser.ParseAudioFileId("AUDIOFILEID: not-a-guid"));
            Assert.Equal(
                new ConnectedTenant("tenant-1", "Contoso AG"),
                TenantOutputParser.ParseConnectedTenant("SUCCESS: Connected to Microsoft Teams\nConnected to tenant: Contoso AG (tenant-1)\n"));
            Assert.Contains("MicrosoftTeams", TenantOutputParser.FindMissingModules("Microsoft.Graph.Users module is available"));
        }

        [Fact]
        public void HolidaySeriesMerge_AddsOnlyEntriesThatAreNotThereYet()
        {
            var existing = new IHolidayEntry[]
            {
                new HolidayDate(new DateTime(2026, 1, 1), TimeSpan.Zero, "Neujahr"),
                new HolidayDate(new DateTime(2026, 12, 24), TimeSpan.Zero, "Betriebsferien", new DateTime(2026, 12, 31), TimeSpan.Zero),
            };
            var candidates = new[]
            {
                new HolidayDate(new DateTime(2026, 1, 1), TimeSpan.Zero, "Neujahr"),
                new HolidayDate(new DateTime(2026, 5, 1), TimeSpan.FromHours(12), "Tag der Arbeit"),
                new HolidayDate(new DateTime(2026, 5, 1), TimeSpan.FromHours(12), "Tag der Arbeit"),
                // Same date as an existing range, but a single day: a different entry.
                new HolidayDate(new DateTime(2026, 12, 24), TimeSpan.Zero, "Betriebsferien"),
            };

            var missing = HolidaySeriesMerge.MissingFrom(existing, candidates);

            Assert.Equal(new[] { candidates[1], candidates[3] }, missing);
            Assert.Empty(HolidaySeriesMerge.MissingFrom(existing.Concat(missing), candidates));
        }

        private static (TenantCommandRunner Runner, Mock<IPowerShellContextService> PowerShell, Mock<IAuditLog> AuditLog, Mock<ISessionManager> Session) CreateRunner()
        {
            var powerShell = new Mock<IPowerShellContextService>(MockBehavior.Strict);
            var auditLog = new Mock<IAuditLog>();
            var session = new Mock<ISessionManager>();
            session.SetupGet(manager => manager.TenantId).Returns("tenant-1");
            var logging = new Mock<ILoggingService>();
            var runner = new TenantCommandRunner(
                powerShell.Object, session.Object, new ThrottleRetryPolicy(logging.Object), logging.Object, auditLog.Object);
            return (runner, powerShell, auditLog, session);
        }
    }
}
