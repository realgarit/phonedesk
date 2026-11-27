using System.Text.Json;
using Moq;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The Edit Settings page: it lists what differs, saves only after a new read that still
    /// matches the check, and opens with the object a list page sent.
    /// </summary>
    public class EditSettingsViewModelTests
    {
        private const string QueueId = "aaaaaaaa-1111-2222-3333-444444444444";

        private readonly ViewModelTestHarness _harness = new();
        private readonly List<string> _changes = new();
        private string _routing = "Attendant";

        public EditSettingsViewModelTests()
        {
            _harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                {
                    if (script.Contains("PDEDIT_JSON", StringComparison.Ordinal))
                    {
                        return new PowerShellExecutionResult
                        {
                            Output = ObjectEdit.ReadMarker + JsonSerializer.Serialize(new
                            {
                                id = QueueId,
                                name = "Support CQ",
                                values = new Dictionary<string, string> { ["routingMethod"] = _routing, ["agentAlertTime"] = "30" },
                                locked = new Dictionary<string, string>(),
                                notes = Array.Empty<string>()
                            })
                        };
                    }

                    _changes.Add(script);
                    _routing = "Serial";
                    return new PowerShellExecutionResult { Output = ObjectEdit.PropertyMarker + "routingMethod\nSUCCESS: saved" };
                });
        }

        private EditSettingsViewModel Page() => new(
            _harness.PowerShellContextService.Object, _harness.PowerShellCommandService.Object, _harness.LoggingService.Object,
            _harness.SessionManager.Object, _harness.NavigationService.Object, _harness.ErrorHandlingService.Object,
            _harness.ValidationService.Object, _harness.SharedStateService.Object, _harness.DialogService.Object,
            translationService: _harness.TranslationService);

        [Fact]
        public async Task Page_SavesOnlyAChangeThatWasCheckedAgainstWhatTheTenantStillHolds()
        {
            var page = Page();
            page.ObjectName = "Support CQ";
            await page.LoadCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "routingMethod", "agentAlertTime" }, page.Fields.Select(row => row.Key));
            Assert.False(page.CanApply);

            // Nothing was changed on the page: there is nothing to save.
            page.CheckCommand.Execute(null);
            Assert.False(page.CanApply);

            page.Fields[0].NewValue = "Serial";
            // A value that changes makes the last check stale.
            Assert.False(page.CanApply);
            page.CheckCommand.Execute(null);
            Assert.True(page.CanApply);
            Assert.Contains("Attendant", page.Summary);

            // Someone set another routing in Teams after the check: nothing is sent.
            _routing = "RoundRobin";
            await page.ApplyCommand.ExecuteAsync(null);
            Assert.Empty(_changes);
            Assert.Equal("RoundRobin", page.Fields[0].Current);
            // The operator's value is kept, and the new list of changes is shown.
            Assert.Equal("Serial", page.Fields[0].NewValue);
            Assert.Contains("RoundRobin", page.Summary);

            await page.ApplyCommand.ExecuteAsync(null);
            Assert.Single(_changes);
            Assert.Equal("Serial", page.Fields[0].Current);
            Assert.False(page.CanApply);
        }

        [Fact]
        public void Page_OpensWithTheObjectAListPageSent()
        {
            EditRequest? request = new(EditObjectKind.AutoAttendant, "Main AA");
            _harness.SharedStateService.SetupGet(state => state.EditRequest).Returns(() => request);
            _harness.SharedStateService.SetupSet(state => state.EditRequest = It.IsAny<EditRequest?>()).Callback<EditRequest?>(value => request = value);

            var page = Page();

            Assert.Equal(1, page.KindIndex);
            Assert.Equal("Main AA", page.ObjectName);
            Assert.Null(request);
        }
    }
}
