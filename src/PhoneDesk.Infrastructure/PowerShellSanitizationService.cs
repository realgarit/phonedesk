using System.Text;
using System.Text.RegularExpressions;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

/// <summary>
/// Service for sanitizing user inputs before they are used in PowerShell commands.
/// Prevents command injection attacks by escaping dangerous characters.
/// </summary>
public partial class PowerShellSanitizationService : IPowerShellSanitizationService
{
    // Pattern for safe identifiers (UPNs, display names, etc.) - now includes extended Latin characters
    [GeneratedRegex(@"^[\p{L}\p{N}\-_\-@.\s]+$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierPattern();

    /// <summary>
    /// Sanitizes a string value for safe use in PowerShell commands.
    /// Escapes single quotes and removes dangerous characters.
    /// Handles Unicode homoglyphs and control characters.
    /// </summary>
    public string SanitizeString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Step 1: Remove null bytes and control characters (ASCII 0-31 and 127)
        var sanitized = RemoveControlCharacters(input);

        // Step 2: Normalize Unicode to avoid homoglyph attacks
        // Normalize to Form C (canonical composition) then check for suspicious characters
        sanitized = sanitized.Normalize(NormalizationForm.FormC);

        // Step 3: Replace Unicode homoglyphs that could be used for injection
        sanitized = ReplaceUnicodeHomoglyphs(sanitized);

        // Step 4: Escape single quotes for PowerShell (double them)
        sanitized = sanitized.Replace("'", "''");

        // Step 5: Remove dangerous PowerShell characters
        sanitized = sanitized.Replace("`", "")   // Backtick (escape character)
                            .Replace("$", "")     // Variable expansion
                            .Replace(";", "")     // Command separator
                            .Replace("|", "")     // Pipeline
                            .Replace("&", "")     // Command separator
                            .Replace("<", "")     // Input redirection
                            .Replace(">", "")     // Output redirection
                            .Replace("\"", "");   // Double quote (prevents string breakout)

        return sanitized;
    }

    /// <summary>
    /// Sanitizes a text that is spoken to callers for use inside a double-quoted PowerShell string.
    /// Only three characters can end or expand such a string: the double quote (with its
    /// typographic forms, which PowerShell reads as a double quote), the dollar sign and the
    /// backtick. They are removed. Apostrophes, plain and typographic, are plain text between
    /// double quotes and stay unchanged, so the text arrives in Teams as it was written.
    /// </summary>
    public string SanitizeSpokenText(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sanitized = RemoveControlCharacters(input).Normalize(NormalizationForm.FormC);
        sanitized = ReplaceUnicodeHomoglyphs(sanitized, keepSingleQuotes: true);
        return sanitized.Replace("`", "")
                        .Replace("$", "")
                        .Replace("\"", "");
    }

    /// <summary>
    /// Validates and sanitizes an identifier (e.g., UPN, display name).
    /// Only allows alphanumeric characters, hyphens, underscores, @ and dots.
    /// Handles Unicode properly for international characters.
    /// </summary>
    public string SanitizeIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Identifier cannot be null or whitespace.", nameof(input));

        // Remove control characters first
        var cleaned = RemoveControlCharacters(input);

        // Normalize Unicode
        cleaned = cleaned.Normalize(NormalizationForm.FormC);

        // Replace homoglyphs
        cleaned = ReplaceUnicodeHomoglyphs(cleaned);

        // Validate using Unicode-aware pattern
        if (!SafeIdentifierPattern().IsMatch(cleaned))
            throw new ArgumentException($"Invalid identifier: {input}. Only letters, numbers, hyphens, underscores, @, dots, and spaces are allowed.", nameof(input));

        // Escape single quotes for PowerShell
        return cleaned.Replace("'", "''");
    }

    /// <summary>
    /// Removes ASCII control characters (0-31 and 127) and other dangerous control codes.
    /// </summary>
    private static string RemoveControlCharacters(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            // Allow common whitespace but remove control characters
            if (c >= 32 || c == '\t' || c == '\n' || c == '\r')
            {
                sb.Append(c);
            }
            // Remove ASCII control chars (0-31 except tab/newline/CR) and DEL (127)
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replaces Unicode characters that look like ASCII punctuation used in PowerShell injection.
    /// These homoglyphs could be used to bypass string-based filters.
    /// </summary>
    private static string ReplaceUnicodeHomoglyphs(string input, bool keepSingleQuotes = false)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            var replacement = c switch
            {
                // Unicode apostrophes and quotes that could bypass filters
                '\u2019' or '\u2018' or '\u201B' or '\u201A' when keepSingleQuotes => c,
                '\u2019' or '\u2018' or '\u201B' or '\u201A' => '\'',  // Various single quote variants
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',   // Various double quote variants
                
                // Unicode backticks and related characters
                '\u02CB' or '\u02C5' or '\u02CE' or '\u02CF' => '`',   // Modifier letter variants
                
                // Unicode semicolons
                '\u037E' or '\uFE54' => ';',  // Greek question mark, small semicolon
                
                // Unicode dollar signs
                '\uFF04' or '\u0024' => '$',  // Fullwidth dollar sign
                
                // Unicode pipes
                '\u01C0' or '\u01C1' or '\uFF5C' => '|',  // Various pipe variants
                
                // Unicode ampersands
                '\uFF06' => '&',  // Fullwidth ampersand
                
                // Unicode angle brackets
                '\u3008' or '\u3009' or '\u27E8' or '\u27E9' => '<',  // Various left angle brackets
                '\u300A' or '\u300B' or '\u27EA' or '\u27EB' => '>',  // Various right angle brackets
                
                _ => c
            };
            sb.Append(replacement);
        }
        return sb.ToString();
    }
}
