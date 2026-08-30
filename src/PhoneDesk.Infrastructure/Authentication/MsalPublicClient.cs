using Microsoft.Identity.Client;

namespace PhoneDesk.Services.Authentication;

internal sealed class MsalPublicClient : IMsalPublicClient
{
    private const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    private readonly IPublicClientApplication _application;

    public MsalPublicClient()
        : this(PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "common")
            .WithRedirectUri("http://localhost")
            .Build())
    {
    }

    internal MsalPublicClient(IPublicClientApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public async Task<IReadOnlyList<IAccount>> GetAccountsAsync()
        => (await _application.GetAccountsAsync()).ToArray();

    public async Task<MsalSilentTokenResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        IAccount account)
    {
        try
        {
            var result = await _application.AcquireTokenSilent(scopes, account).ExecuteAsync();
            return new MsalSilentTokenResult(
                new MsalTokenResult(result.AccessToken, result.Account?.Username),
                RequiresInteraction: false);
        }
        catch (MsalUiRequiredException)
        {
            return new MsalSilentTokenResult(Token: null, RequiresInteraction: true);
        }
    }

    public async Task<MsalTokenResult?> AcquireTokenInteractiveAsync(
        IReadOnlyList<string> scopes,
        IntPtr? parentWindowHandle)
    {
        var builder = _application
            .AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false);

        if (parentWindowHandle.HasValue && parentWindowHandle.Value != IntPtr.Zero)
        {
            builder = builder.WithParentActivityOrWindow(parentWindowHandle.Value);
        }

        var result = await builder.ExecuteAsync();
        return result is null
            ? null
            : new MsalTokenResult(result.AccessToken, result.Account?.Username);
    }

    public Task RemoveAsync(IAccount account) => _application.RemoveAsync(account);
}
