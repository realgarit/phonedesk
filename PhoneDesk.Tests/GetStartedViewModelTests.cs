using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class GetStartedViewModelTests
    {
        private readonly Mock<IMsalGraphAuthenticationService> _msalAuthService = new();

        private GetStartedViewModel CreateViewModel(ViewModelTestHarness harness, PhoneDesk.Operations.TenantIdentityGuard guard) =>
            new GetStartedViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                _msalAuthService.Object,
                translationService: harness.TranslationService,
                identityGuard: guard);

        [Fact]
        public async Task ConnectTeams_ToAnotherOrganizationThanExpected_IsNotReadyAndSaysWhy()
        {
            // The sign-in works, but the browser kept the session of another customer.
            var harness = new ViewModelTestHarness();
            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string? script, Dictionary<string, string>? _, IProgress<PowerShellProgress>? _, CancellationToken _) =>
                    new PowerShellExecutionResult
                    {
                        // The mocked command service returns no script text for the connect command.
                        Output = script is not null && script.Contains("PDIDENTITY_JSON", StringComparison.Ordinal)
                            ? "PDIDENTITY_JSON: {\"teamsTenantId\":\"11111111-1111-1111-1111-111111111111\",\"teamsTenantName\":\"Contoso\",\"teamsDomains\":[\"contoso.com\"],\"graphTenantId\":\"11111111-1111-1111-1111-111111111111\",\"graphAccount\":\"admin@contoso.com\"}"
                            : "Connected to tenant: Contoso (11111111-1111-1111-1111-111111111111)\nSUCCESS"
                    });
            harness.SessionManager.SetupGet(session => session.ModulesChecked).Returns(true);
            var guard = new PhoneDesk.Operations.TenantIdentityGuard();
            var vm = CreateViewModel(harness, guard);
            vm.ModulesChecked = true;
            vm.GraphConnected = true;
            vm.ExpectedTenant = "fabrikam.com";

            await vm.ConnectTeamsCommand.ExecuteAsync(null);

            Assert.True(vm.TeamsConnected);
            Assert.False(vm.CanProceed);
            Assert.True(vm.HasIdentityProblem);
            Assert.Equal("You expected fabrikam.com, but the sign-in reached Contoso.", vm.IdentityProblemText);
            Assert.StartsWith("The sign-ins worked, but they are not the expected ones.", vm.SetupGuidance);
            Assert.Contains("Microsoft Teams: Contoso (11111111-1111-1111-1111-111111111111)", vm.IdentityText);

            // The operator corrects the expectation: the same connections are ready.
            vm.ExpectedTenant = "contoso.com";
            Assert.False(vm.HasIdentityProblem);
            Assert.True(vm.CanProceed);
        }

        [Fact]
        public async Task RestartTeamsSignIn_GivesUpTheSessionThatHoldsTheSignIn()
        {
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetConnectTeamsCommand()).Returns("CONNECT TEAMS");
            var hanging = new TaskCompletionSource<PowerShellExecutionResult>();
            harness.PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    "CONNECT TEAMS", It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()))
                .Returns(hanging.Task);
            harness.PowerShellContextService.Setup(p => p.AbandonSession()).Returns(true);
            harness.SessionManager.SetupGet(s => s.ModulesChecked).Returns(true);
            var vm = CreateViewModel(harness, new PhoneDesk.Operations.TenantIdentityGuard());
            vm.ModulesChecked = true;

            var connecting = vm.ConnectTeamsCommand.ExecuteAsync(null);
            Assert.True(vm.TeamsSignInPending);

            vm.RestartTeamsSignInCommand.Execute(null);
            await connecting;

            harness.PowerShellContextService.Verify(p => p.AbandonSession(), Times.Once);
            Assert.False(vm.TeamsSignInPending);
            Assert.False(vm.TeamsConnected);
            Assert.False(vm.GraphConnected);
            Assert.False(vm.IsBusy);
            Assert.Contains("new PowerShell session", vm.SetupGuidance);
            // The module check stays: the new session can connect at once.
            harness.SessionManager.Verify(s => s.UpdateModulesChecked(true), Times.Once);

            // The old window is finished later: nothing is connected by it.
            hanging.SetResult(new PowerShellExecutionResult { Output = "SUCCESS: Connected to Microsoft Teams" });
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(vm.TeamsConnected);
            harness.SessionManager.Verify(s => s.UpdateTeamsConnection(true, It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task RestartGraphSignIn_GivesUpASignInThatHangsAndItsLateResultConnectsNothing()
        {
            var harness = new ViewModelTestHarness();
            var hanging = new TaskCompletionSource<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)>();
            _msalAuthService.Setup(m => m.AuthenticateAsync(It.IsAny<nint?>())).Returns(hanging.Task);
            var vm = CreateViewModel(harness, new PhoneDesk.Operations.TenantIdentityGuard());
            vm.ModulesChecked = true;

            var connecting = vm.ConnectGraphCommand.ExecuteAsync(null);
            Assert.True(vm.GraphSignInPending);

            vm.RestartGraphSignInCommand.Execute(null);
            await connecting;

            Assert.False(vm.GraphSignInPending);
            Assert.False(vm.GraphConnected);
            Assert.False(vm.IsBusy);
            Assert.Contains("given up", vm.SetupGuidance);

            // The old window is finished later: nothing is connected by it.
            hanging.SetResult((true, "old-token", "someone@fabrikam.com", null));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(vm.GraphConnected);
            harness.SessionManager.Verify(s => s.UpdateGraphConnection(true, It.IsAny<string?>()), Times.Never);
        }

        private GetStartedViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new GetStartedViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                _msalAuthService.Object);

        private static string ModulesAvailableOutput() =>
            "MicrosoftTeams module is available\n" +
            $"{ConstantsService.PowerShellModules.MicrosoftGraphAuthentication} module is available\n" +
            $"{ConstantsService.PowerShellModules.MicrosoftGraphUsers} module is available\n" +
            $"{ConstantsService.PowerShellModules.MicrosoftGraphUsersActions} module is available\n" +
            $"{ConstantsService.PowerShellModules.MicrosoftGraphGroups} module is available\n" +
            $"{ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement} module is available";

        [Fact]
        public void Construction_InitializesFromSessionManagerState()
        {
            var harness = new ViewModelTestHarness();
            harness.SessionManager.SetupGet(s => s.ModulesChecked).Returns(true);
            harness.SessionManager.SetupGet(s => s.TeamsConnected).Returns(true);
            harness.SessionManager.SetupGet(s => s.GraphConnected).Returns(false);

            var vm = CreateViewModel(harness);

            Assert.True(vm.ModulesChecked);
            Assert.True(vm.TeamsConnected);
            Assert.False(vm.GraphConnected);
            Assert.False(vm.CanProceed);
            Assert.Contains("Graph next", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Connect Graph", vm.GraphActionText);
            harness.LoggingService.Verify(l => l.Log("Get Started page loaded", LogLevel.Info), Times.Once);
        }

        [Fact]
        public void Construction_EmptySession_ExplainsFirstAction()
        {
            var harness = new ViewModelTestHarness();

            var vm = CreateViewModel(harness);

            Assert.Contains("Check modules", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Check modules first", vm.TeamsActionText);
            Assert.Equal("Check modules first", vm.GraphActionText);
        }

        [Fact]
        public async Task CheckModulesAsync_AllModulesAvailable_SetsModulesCheckedTrue()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult(ModulesAvailableOutput());
            var vm = CreateViewModel(harness);

            await vm.CheckModulesCommand.ExecuteAsync(null);

            Assert.True(vm.ModulesChecked);
            Assert.Contains("Teams next", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Connect Teams", vm.TeamsActionText);
            harness.SessionManager.Verify(s => s.UpdateModulesChecked(true), Times.Once);
        }

        [Fact]
        public async Task CheckModulesAsync_MissingModule_SetsModulesCheckedFalse()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("MicrosoftTeams module is available");
            var vm = CreateViewModel(harness);

            await vm.CheckModulesCommand.ExecuteAsync(null);

            Assert.False(vm.ModulesChecked);
            Assert.Contains("try again", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
            harness.SessionManager.Verify(s => s.UpdateModulesChecked(false), Times.Once);
        }

        [Fact]
        public void Construction_AllChecksPassed_ExplainsWhatToDoNext()
        {
            var harness = new ViewModelTestHarness();
            harness.SessionManager.SetupGet(s => s.ModulesChecked).Returns(true);
            harness.SessionManager.SetupGet(s => s.TeamsConnected).Returns(true);
            harness.SessionManager.SetupGet(s => s.GraphConnected).Returns(true);

            var vm = CreateViewModel(harness);

            Assert.True(vm.CanProceed);
            Assert.Contains("ready", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ConnectGraphAsync_MsalAuthFails_ExplainsBrowserSignInRecovery()
        {
            var harness = new ViewModelTestHarness();
            _msalAuthService.Setup(m => m.AuthenticateAsync(It.IsAny<nint?>()))
                .ReturnsAsync((false, null, null, "auth failed"));
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectGraphCommand.ExecuteAsync(null);

            Assert.Contains("sign-in", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("try again", vm.SetupGuidance, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ConnectTeamsAsync_ModulesNotChecked_DoesNotExecutePowerShell()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.ConnectTeamsCommand.ExecuteAsync(null);

            Assert.False(vm.TeamsConnected);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ConnectTeamsAsync_HappyPath_SetsTeamsConnectedAndUpdatesSession()
        {
            var harness = new ViewModelTestHarness();
            harness.SessionManager.SetupGet(s => s.ModulesChecked).Returns(true);
            // The tenant-info line must be its own line without a leading "SUCCESS:" marker: the VM's
            // parser does `line.Split(':')[1]` on the "Connected to tenant:" line, so a leading marker
            // on the same line would shift the split index. The "SUCCESS" marker itself just needs to
            // appear somewhere in the combined output for HasSuccessMarker to be true.
            harness.SetExecutionResult("Connected to tenant: Contoso (11111111-1111-1111-1111-111111111111)\nSUCCESS");
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectTeamsCommand.ExecuteAsync(null);

            Assert.True(vm.TeamsConnected);
            harness.SessionManager.Verify(s => s.UpdateTeamsConnection(true), Times.Once);
            harness.SessionManager.Verify(s => s.UpdateTenantInfo("11111111-1111-1111-1111-111111111111", "Contoso"), Times.Once);
        }

        [Fact]
        public async Task ConnectTeamsAsync_ErrorPath_SetsTeamsConnectedFalse()
        {
            var harness = new ViewModelTestHarness();
            harness.SetExecutionResult("ERROR: connection failed");
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectTeamsCommand.ExecuteAsync(null);

            Assert.False(vm.TeamsConnected);
            harness.SessionManager.Verify(s => s.UpdateTeamsConnection(false), Times.Once);
        }

        [Fact]
        public async Task ConnectTeamsAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectTeamsCommand.ExecuteAsync(null);

            Assert.False(vm.TeamsConnected);
            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task ConnectGraphAsync_ModulesNotChecked_DoesNotAuthenticate()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.ConnectGraphCommand.ExecuteAsync(null);

            Assert.False(vm.GraphConnected);
            _msalAuthService.Verify(m => m.AuthenticateAsync(It.IsAny<nint?>()), Times.Never);
        }

        [Fact]
        public async Task ConnectGraphAsync_MsalAuthFails_SetsGraphConnectedFalse()
        {
            var harness = new ViewModelTestHarness();
            _msalAuthService.Setup(m => m.AuthenticateAsync(It.IsAny<nint?>()))
                .ReturnsAsync((false, null, null, "auth failed"));
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectGraphCommand.ExecuteAsync(null);

            Assert.False(vm.GraphConnected);
            harness.SessionManager.Verify(s => s.UpdateGraphConnection(false), Times.Once);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ConnectGraphAsync_HappyPath_SetsGraphConnectedTrue()
        {
            var harness = new ViewModelTestHarness();
            _msalAuthService.Setup(m => m.AuthenticateAsync(It.IsAny<nint?>()))
                .ReturnsAsync((true, "token123", "user@contoso.com", null));
            harness.SetExecutionResult("SUCCESS: Connected to Graph");
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectGraphCommand.ExecuteAsync(null);

            Assert.True(vm.GraphConnected);
            harness.SessionManager.Verify(s => s.UpdateGraphConnection(true, "user@contoso.com"), Times.Once);
        }

        [Fact]
        public async Task ConnectGraphAsync_SessionExpired_ReportsConnectionError()
        {
            var harness = new ViewModelTestHarness();
            _msalAuthService.Setup(m => m.AuthenticateAsync(It.IsAny<nint?>()))
                .ReturnsAsync((true, "token123", "user@contoso.com", null));
            harness.SetSessionExpired();
            var vm = CreateViewModel(harness);
            vm.ModulesChecked = true;

            await vm.ConnectGraphCommand.ExecuteAsync(null);

            Assert.False(vm.GraphConnected);
            harness.ErrorHandlingService.Verify(e => e.HandleConnectionError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task DisconnectTeamsAsync_SetsTeamsConnectedFalseAndUpdatesSession()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.TeamsConnected = true;

            await vm.DisconnectTeamsCommand.ExecuteAsync(null);

            Assert.False(vm.TeamsConnected);
            harness.SessionManager.Verify(s => s.UpdateTeamsConnection(false), Times.Once);
        }

        [Fact]
        public async Task DisconnectGraphAsync_SignsOutOfMsalAndUpdatesSession()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.GraphConnected = true;

            await vm.DisconnectGraphCommand.ExecuteAsync(null);

            Assert.False(vm.GraphConnected);
            _msalAuthService.Verify(m => m.SignOutAsync(), Times.Once);
            harness.SessionManager.Verify(s => s.UpdateGraphConnection(false), Times.Once);
        }

        [Fact]
        public void NavigateToPageCommand_CanProceedFalse_DoesNotNavigate()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.NavigateToPageCommand.Execute("Variables");

            harness.NavigationService.Verify(n => n.NavigateTo(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void NavigateToPageCommand_CanProceedTrue_Navigates()
        {
            var harness = new ViewModelTestHarness();
            harness.SessionManager.SetupGet(s => s.ModulesChecked).Returns(true);
            harness.SessionManager.SetupGet(s => s.TeamsConnected).Returns(true);
            harness.SessionManager.SetupGet(s => s.GraphConnected).Returns(true);
            var vm = CreateViewModel(harness);

            vm.NavigateToPageCommand.Execute("Variables");

            harness.NavigationService.Verify(n => n.NavigateTo("Variables"), Times.Once);
        }
    }
}
