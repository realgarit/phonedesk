using System.Text.Json;
using Moq;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// The Phone Numbers page: it lists the inventory, checks a change without sending anything,
    /// moves a number only after the operator confirmed the move, and reports from a new read.
    /// </summary>
    public class PhoneNumbersViewModelTests
    {
        private const string Anna = "11111111-aaaa-0000-0000-000000000001";
        private const string Ben = "22222222-bbbb-0000-0000-000000000002";
        private const string Number = "+41441110001";

        /// <summary>A tenant for the page: the list and the check return what is set, every other script is a change.</summary>
        private sealed class Tenant
        {
            public string List { get; set; } = string.Empty;
            public string Check { get; set; } = string.Empty;
            public List<string> Changes { get; } = new();
            public Action? OnChange { get; set; }

            public void Attach(ViewModelTestHarness harness)
                => harness.PowerShellContextService
                    .Setup(p => p.ExecuteCommandWithDetailsAsync(
                        It.IsAny<string>(),
                        It.IsAny<Dictionary<string, string>?>(),
                        It.IsAny<IProgress<PowerShellProgress>?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                    {
                        if (script.Contains("PDNUMBERS_JSON", StringComparison.Ordinal))
                        {
                            return new PowerShellExecutionResult { Output = List };
                        }

                        if (script.Contains("PDNUMBERCHECK_JSON", StringComparison.Ordinal))
                        {
                            return new PowerShellExecutionResult { Output = Check };
                        }

                        Changes.Add(script);
                        OnChange?.Invoke();
                        return new PowerShellExecutionResult { Output = "SUCCESS: done" };
                    });
        }

        private static PhoneNumbersViewModel CreateViewModel(ViewModelTestHarness harness)
            => new PhoneNumbersViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                translationService: harness.TranslationService);

        [Fact]
        public async Task Load_ListsNumbersWithTheirOwner()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { List = ListOutput() };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.IsLoaded);
            Assert.Equal(2, vm.Numbers.Count);
            Assert.Equal("Anna Muster (anna@contoso.com)", vm.Numbers[0].AssignedTo);
            Assert.Equal("Assigned", vm.Numbers[0].Status);
            Assert.Equal("Free", vm.Numbers[1].Status);
            Assert.Equal("2 numbers: 1 assigned, 1 free.", vm.Summary);
        }

        [Fact]
        public async Task Move_NeedsTheConfirmationAndReportsWhoHasTheNumberAfterwards()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { List = ListOutput(), Check = CheckOutput(Anna) };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            await vm.LoadCommand.ExecuteAsync(null);
            vm.SelectedNumber = vm.Numbers[0];

            vm.OpenAssignCommand.Execute(null);
            Assert.Equal(Number, vm.DialogNumber);
            vm.DialogTarget = "ben@contoso.com";

            await vm.CheckCommand.ExecuteAsync(null);
            Assert.False(vm.CanApply);
            Assert.Contains("Has the number now: Anna Muster (anna@contoso.com)", vm.DialogPreview);
            Assert.Contains("Cannot be done:", vm.DialogPreview);

            vm.AllowReassign = true;
            await vm.CheckCommand.ExecuteAsync(null);
            Assert.True(vm.CanApply);
            Assert.Contains("the account that has it now loses it", vm.DialogPreview);
            Assert.Empty(tenant.Changes);

            // The change runs, but the tenant still shows the old owner: the status says so.
            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.Contains("Remove-CsPhoneNumberAssignment -Identity '" + Anna + "'", Assert.Single(tenant.Changes));
            Assert.False(vm.ShowDialog);
            Assert.StartsWith("The tenant does not show the change:", vm.StatusMessage);

            // A second run where the tenant shows the new owner.
            vm.SelectedNumber = vm.Numbers[0];
            vm.OpenAssignCommand.Execute(null);
            vm.DialogTarget = "ben@contoso.com";
            vm.AllowReassign = true;
            await vm.CheckCommand.ExecuteAsync(null);
            tenant.OnChange = () => tenant.Check = CheckOutput(Ben);
            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.Equal("Saved. The number was read again and shows the change.", vm.StatusMessage);
        }

        [Fact]
        public async Task Check_RefusesTextThatIsNotANumberOrAnAccount()
        {
            var harness = new ViewModelTestHarness();
            var tenant = new Tenant { List = ListOutput(), Check = CheckOutput(Anna) };
            tenant.Attach(harness);
            var vm = CreateViewModel(harness);
            vm.OpenAssignCommand.Execute(null);
            vm.DialogNumber = "044 111 00 01";
            vm.DialogTarget = "ben@contoso.com";

            await vm.CheckCommand.ExecuteAsync(null);
            Assert.False(vm.CanApply);
            Assert.Contains("E.164", vm.DialogPreview);

            vm.DialogNumber = Number;
            vm.DialogTarget = "Ben Beispiel";
            await vm.CheckCommand.ExecuteAsync(null);
            Assert.False(vm.CanApply);
            Assert.Contains("not a user principal name", vm.DialogPreview);
        }

        private static object Owner(string id, string upn, string name, string? number)
            => new { id, userPrincipalName = upn, displayName = name, accountType = "User", phoneNumber = number ?? string.Empty, featureTypes = new[] { "PhoneSystem" } };

        private static string ListOutput()
            => PhoneNumbers.ListMarker + JsonSerializer.Serialize(new object[]
            {
                new { number = Number, type = "CallingPlan", status = "UserAssigned", assignedToId = Anna, city = "", capabilities = new[] { "UserAssignment" }, owner = Owner(Anna, "anna@contoso.com", "Anna Muster", Number) },
                new { number = "+41441110002", type = "CallingPlan", status = "Unassigned", assignedToId = "", city = "", capabilities = new[] { "UserAssignment" }, owner = (object?)null }
            }) + "\nSUCCESS: Phone numbers retrieved";

        private static string CheckOutput(string ownerId)
            => PhoneNumbers.CheckMarker + JsonSerializer.Serialize(new
            {
                number = new
                {
                    number = Number,
                    type = "CallingPlan",
                    status = "UserAssigned",
                    assignedToId = ownerId,
                    city = "",
                    capabilities = new[] { "UserAssignment" },
                    owner = ownerId == Anna ? Owner(Anna, "anna@contoso.com", "Anna Muster", Number) : Owner(Ben, "ben@contoso.com", "Ben Beispiel", Number)
                },
                target = Owner(Ben, "ben@contoso.com", "Ben Beispiel", ownerId == Ben ? Number : null),
                targetError = (string?)null
            }) + "\nSUCCESS: Phone number retrieved";
    }
}
