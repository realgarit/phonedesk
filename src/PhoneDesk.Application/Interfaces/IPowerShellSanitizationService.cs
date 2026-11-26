namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Service for sanitizing user inputs before they are used in PowerShell commands.
/// Prevents command injection attacks by escaping dangerous characters.
/// </summary>
public interface IPowerShellSanitizationService
{
    /// <summary>
    /// Sanitizes a string value for safe use in PowerShell commands.
    /// Escapes single quotes and removes dangerous characters.
    /// </summary>
    string SanitizeString(string input);

    /// <summary>
    /// Sanitizes a text that is spoken to callers (a greeting or prompt) for use inside a
    /// double-quoted PowerShell string. Apostrophes stay as they are: in a double-quoted string
    /// they are plain text, and a doubled apostrophe would be stored and spoken as two.
    /// Use this only where the value is written between double quotes.
    /// </summary>
    string SanitizeSpokenText(string input);

    /// <summary>
    /// Validates and sanitizes an identifier (e.g., UPN, display name).
    /// Only allows alphanumeric characters, hyphens, underscores, @ and dots.
    /// </summary>
    string SanitizeIdentifier(string input);
}
