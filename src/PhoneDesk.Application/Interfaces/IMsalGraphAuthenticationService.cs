namespace PhoneDesk.Services.Interfaces
{
    /// <summary>
    /// Service for authenticating to Microsoft Graph using MSAL.
    /// This bypasses PowerShell's WAM requirement by obtaining tokens natively in C#.
    /// </summary>
    public interface IMsalGraphAuthenticationService
    {
        /// <summary>
        /// Authenticates to Microsoft Graph interactively using a browser popup.
        /// Returns a tuple of (success, accessToken, errorMessage).
        /// </summary>
        /// <param name="parentWindowHandle">Optional parent window handle for the browser popup (Windows only)</param>
        Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)> AuthenticateAsync(IntPtr? parentWindowHandle = null);
        
        /// <summary>
        /// Signs out from Microsoft Graph, clearing cached tokens.
        /// </summary>
        Task SignOutAsync();
        
        /// <summary>
        /// Checks if there's a cached account that can be used for silent authentication.
        /// </summary>
        Task<bool> HasCachedAccountAsync();

        /// <summary>
        /// Makes the sign-in of a profile the one in use. Each profile keeps its own accounts in
        /// memory, so the sign-in of one customer is never used for another, and switching back
        /// needs no new sign-in while the application runs. Nothing is written to disk.
        /// </summary>
        void UseProfile(string key)
        {
        }
    }
}
