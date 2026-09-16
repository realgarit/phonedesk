using Microsoft.Identity.Client;
using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Authentication;
using PhoneDeskLogLevel = PhoneDesk.Services.LogLevel;

namespace PhoneDesk.IntegrationTests;

public sealed class MsalGraphAuthenticationServiceIntegrationTests
{
    private static readonly string[] ExpectedScopes =
    {
        "User.ReadWrite.All",
        "Organization.Read.All",
        "Group.ReadWrite.All",
        "Directory.ReadWrite.All"
    };

    [Fact]
    public async Task CachedAccountUsesSilentAuthenticationWithoutOpeningInteractiveFlow()
    {
        var account = CreateAccount("admin@contoso.example");
        var client = new FakeMsalPublicClient
        {
            Accounts = new[] { account },
            SilentResult = new MsalSilentTokenResult(
                new MsalTokenResult("token", "admin@contoso.example"),
                RequiresInteraction: false)
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        var result = await service.AuthenticateAsync();

        Assert.True(result.Success);
        Assert.Equal("token", result.AccessToken);
        Assert.Equal("admin@contoso.example", result.Account);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, client.SilentCalls);
        Assert.Equal(0, client.InteractiveCalls);
        Assert.Equal(ExpectedScopes, client.LastScopes);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "Attempting silent authentication for cached account: admin@contoso.example" &&
            entry.Level == PhoneDeskLogLevel.Info);
    }

    [Fact]
    public async Task MissingCachedAccountUsesInteractiveAuthenticationAndPassesParentHandle()
    {
        var client = new FakeMsalPublicClient
        {
            InteractiveResult = new MsalTokenResult("interactive-token", "operator@contoso.example")
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);
        var parentHandle = new IntPtr(42);

        var result = await service.AuthenticateAsync(parentHandle);

        Assert.True(result.Success);
        Assert.Equal("interactive-token", result.AccessToken);
        Assert.Equal("operator@contoso.example", result.Account);
        Assert.Equal(0, client.SilentCalls);
        Assert.Equal(1, client.InteractiveCalls);
        Assert.Equal(parentHandle, client.LastParentWindowHandle);
        Assert.Equal(ExpectedScopes, client.LastScopes);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "Opening browser for authentication..." && entry.Level == PhoneDeskLogLevel.Info);
    }

    [Fact]
    public async Task UiRequiredSilentResultFallsBackToInteractiveAuthentication()
    {
        var account = CreateAccount("cached@contoso.example");
        var client = new FakeMsalPublicClient
        {
            Accounts = new[] { account },
            SilentResult = new MsalSilentTokenResult(Token: null, RequiresInteraction: true),
            InteractiveResult = new MsalTokenResult("fallback-token", "cached@contoso.example")
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        var result = await service.AuthenticateAsync();

        Assert.True(result.Success);
        Assert.Equal("fallback-token", result.AccessToken);
        Assert.Equal(1, client.SilentCalls);
        Assert.Equal(1, client.InteractiveCalls);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "Silent authentication failed, falling back to interactive..." &&
            entry.Level == PhoneDeskLogLevel.Info);
    }

    [Fact]
    public async Task EmptyInteractiveTokenReturnsExistingHandledFailure()
    {
        var client = new FakeMsalPublicClient
        {
            InteractiveResult = new MsalTokenResult(string.Empty, "operator@contoso.example")
        };
        var service = new MsalGraphAuthenticationService(new TestLoggingService(), client);

        var result = await service.AuthenticateAsync();

        Assert.False(result.Success);
        Assert.Null(result.AccessToken);
        Assert.Null(result.Account);
        Assert.Equal("Authentication completed but no token received", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticationCancellationReturnsExistingHandledFailure()
    {
        var client = new FakeMsalPublicClient
        {
            InteractiveException = new MsalClientException("authentication_canceled", "cancelled by test")
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        var result = await service.AuthenticateAsync();

        Assert.False(result.Success);
        Assert.Equal("Authentication was canceled", result.ErrorMessage);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "Authentication was canceled by user" && entry.Level == PhoneDeskLogLevel.Warning);
    }

    [Fact]
    public async Task MsalServiceFailureReturnsExistingServiceError()
    {
        var client = new FakeMsalPublicClient
        {
            InteractiveException = new MsalServiceException("service_error", "service unavailable")
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        var result = await service.AuthenticateAsync();

        Assert.False(result.Success);
        Assert.Equal("MSAL service error: service unavailable", result.ErrorMessage);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "MSAL service error: service unavailable" && entry.Level == PhoneDeskLogLevel.Error);
    }

    [Fact]
    public async Task UnexpectedAuthenticationFailureReturnsExistingGenericError()
    {
        var client = new FakeMsalPublicClient
        {
            InteractiveException = new InvalidOperationException("unexpected failure")
        };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        var result = await service.AuthenticateAsync();

        Assert.False(result.Success);
        Assert.Equal("Authentication error: unexpected failure", result.ErrorMessage);
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "Authentication error: unexpected failure" && entry.Level == PhoneDeskLogLevel.Error);
    }

    [Fact]
    public async Task HasCachedAccountReflectsTheAdapterAccountList()
    {
        var emptyService = new MsalGraphAuthenticationService(
            new TestLoggingService(),
            new FakeMsalPublicClient());
        var cachedService = new MsalGraphAuthenticationService(
            new TestLoggingService(),
            new FakeMsalPublicClient { Accounts = new[] { CreateAccount("cached@contoso.example") } });

        Assert.False(await emptyService.HasCachedAccountAsync());
        Assert.True(await cachedService.HasCachedAccountAsync());
    }

    [Fact]
    public async Task SignOutRemovesEveryCachedAccountAndPreservesLogText()
    {
        var first = CreateAccount("first@contoso.example");
        var second = CreateAccount("second@contoso.example");
        var client = new FakeMsalPublicClient { Accounts = new[] { first, second } };
        var logger = new TestLoggingService();
        var service = new MsalGraphAuthenticationService(logger, client);

        await service.SignOutAsync();

        Assert.Equal(new[] { first, second }, client.RemovedAccounts);
        Assert.Contains(logger.Entries, entry => entry.Message == "Removed cached account: first@contoso.example");
        Assert.Contains(logger.Entries, entry => entry.Message == "Removed cached account: second@contoso.example");
    }

    private static IAccount CreateAccount(string username)
    {
        var account = new Mock<IAccount>();
        account.SetupGet(value => value.Username).Returns(username);
        return account.Object;
    }

    private sealed class FakeMsalPublicClient : IMsalPublicClient
    {
        public IReadOnlyList<IAccount> Accounts { get; init; } = Array.Empty<IAccount>();
        public MsalSilentTokenResult SilentResult { get; init; } = new(Token: null, RequiresInteraction: true);
        public MsalTokenResult? InteractiveResult { get; init; }
        public Exception? InteractiveException { get; init; }
        public int SilentCalls { get; private set; }
        public int InteractiveCalls { get; private set; }
        public IReadOnlyList<string>? LastScopes { get; private set; }
        public IntPtr? LastParentWindowHandle { get; private set; }
        public List<IAccount> RemovedAccounts { get; } = new();

        public Task<IReadOnlyList<IAccount>> GetAccountsAsync()
            => Task.FromResult(Accounts);

        public Task<MsalSilentTokenResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            IAccount account)
        {
            SilentCalls++;
            LastScopes = scopes;
            return Task.FromResult(SilentResult);
        }

        public Task<MsalTokenResult?> AcquireTokenInteractiveAsync(
            IReadOnlyList<string> scopes,
            IntPtr? parentWindowHandle)
        {
            InteractiveCalls++;
            LastScopes = scopes;
            LastParentWindowHandle = parentWindowHandle;
            if (InteractiveException is not null)
            {
                return Task.FromException<MsalTokenResult?>(InteractiveException);
            }

            return Task.FromResult(InteractiveResult);
        }

        public Task RemoveAsync(IAccount account)
        {
            RemovedAccounts.Add(account);
            return Task.CompletedTask;
        }
    }
}
