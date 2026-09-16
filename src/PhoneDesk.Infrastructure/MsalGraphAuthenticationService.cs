using Microsoft.Identity.Client;
using PhoneDesk.Services.Authentication;
using PhoneDesk.Services.Interfaces;


namespace PhoneDesk.Services
{
    /// <summary>
    /// Authenticates to Microsoft Graph using MSAL.NET with a browser popup.
    /// This bypasses the WAM (Web Account Manager) issues on Windows by using
    /// interactive browser authentication with system browser.
    /// </summary>
    public class MsalGraphAuthenticationService : IMsalGraphAuthenticationService
    {
        private readonly ILoggingService _loggingService;
        private readonly IMsalPublicClient _msalClient;
        
        // Required scopes for the app
        private static readonly string[] Scopes = new[]
        {
            "User.ReadWrite.All",
            "Organization.Read.All",
            "Group.ReadWrite.All",
            "Directory.ReadWrite.All"
        };
        
        public MsalGraphAuthenticationService(ILoggingService loggingService)
            : this(loggingService, new MsalPublicClient())
        {
        }

        internal MsalGraphAuthenticationService(
            ILoggingService loggingService,
            IMsalPublicClient msalClient)
        {
            _loggingService = loggingService;
            _msalClient = msalClient;
            _loggingService.Log("MSAL Graph authentication service initialized", LogLevel.Info);
        }
        
        public async Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)> AuthenticateAsync(IntPtr? parentWindowHandle = null)
        {
            try
            {
                _loggingService.Log("Starting MSAL interactive authentication...", LogLevel.Info);
                
                MsalTokenResult? result = null;
                
                // First, try silent authentication with cached accounts
                var accounts = await _msalClient.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();
                
                if (firstAccount != null)
                {
                    _loggingService.Log($"Attempting silent authentication for cached account: {firstAccount.Username}", LogLevel.Info);
                    var silentResult = await _msalClient.AcquireTokenSilentAsync(Scopes, firstAccount);
                    if (silentResult.RequiresInteraction)
                    {
                        _loggingService.Log("Silent authentication failed, falling back to interactive...", LogLevel.Info);
                    }

                    result = silentResult.Token;
                }
                
                // If silent failed or no cached account, do interactive
                if (result == null)
                {
                    _loggingService.Log("Opening browser for authentication...", LogLevel.Info);
                    result = await _msalClient.AcquireTokenInteractiveAsync(Scopes, parentWindowHandle);
                }
                
                if (result != null && !string.IsNullOrEmpty(result.AccessToken))
                {
                    _loggingService.Log($"Authentication successful for account: {result.Username}", LogLevel.Success);
                    return (true, result.AccessToken, result.Username, null);
                }
                
                return (false, null, null, "Authentication completed but no token received");
            }
            catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
            {
                _loggingService.Log("Authentication was canceled by user", LogLevel.Warning);
                return (false, null, null, "Authentication was canceled");
            }
            catch (MsalServiceException ex)
            {
                var errorMessage = $"MSAL service error: {ex.Message}";
                _loggingService.Log(errorMessage, LogLevel.Error);
                return (false, null, null, errorMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Authentication error: {ex.Message}";
                _loggingService.Log(errorMessage, LogLevel.Error);
                return (false, null, null, errorMessage);
            }
        }
        
        public async Task SignOutAsync()
        {
            try
            {
                var accounts = await _msalClient.GetAccountsAsync();
                foreach (var account in accounts)
                {
                    await _msalClient.RemoveAsync(account);
                    _loggingService.Log($"Removed cached account: {account.Username}", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error during sign out: {ex.Message}", LogLevel.Warning);
            }
        }
        
        public async Task<bool> HasCachedAccountAsync()
        {
            var accounts = await _msalClient.GetAccountsAsync();
            return accounts.Any();
        }
    }
}
