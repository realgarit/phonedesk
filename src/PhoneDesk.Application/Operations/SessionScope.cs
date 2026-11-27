namespace PhoneDesk.Operations;

/// <summary>
/// Binds the commands of one change to the PowerShell session it was checked for. A change is
/// checked against a profile (its identity, its plan); when the profile is switched before the
/// change runs, the host refuses the command instead of sending it to the other customer's
/// session. The binding flows with the async call, so every command of the change is covered.
/// </summary>
public static class SessionScope
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The session key the running change was checked for, or null outside a change.</summary>
    public static string? Expected => Current.Value;

    public static IDisposable Expect(string sessionKey)
    {
        var previous = Current.Value;
        Current.Value = sessionKey ?? string.Empty;
        return new Restore(previous);
    }

    /// <summary>The text of the result the host returns for a command it refused.</summary>
    public const string SwitchedMessage =
        "ERROR: Another profile was taken into use after this change was checked. Nothing was sent. Check the change again for the profile in use.";

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() => Current.Value = _previous;
    }
}
