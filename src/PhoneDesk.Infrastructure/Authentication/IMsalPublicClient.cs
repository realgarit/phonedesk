using Microsoft.Identity.Client;

namespace PhoneDesk.Services.Authentication;

internal interface IMsalPublicClient
{
    Task<IReadOnlyList<IAccount>> GetAccountsAsync();

    Task<MsalSilentTokenResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        IAccount account);

    Task<MsalTokenResult?> AcquireTokenInteractiveAsync(
        IReadOnlyList<string> scopes,
        IntPtr? parentWindowHandle);

    Task RemoveAsync(IAccount account);
}

internal sealed record MsalTokenResult(string AccessToken, string? Username);

internal sealed record MsalSilentTokenResult(MsalTokenResult? Token, bool RequiresInteraction);
