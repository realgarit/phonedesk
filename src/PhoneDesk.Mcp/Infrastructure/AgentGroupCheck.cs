namespace PhoneDesk.Mcp;

/// <summary>
/// The call queue script takes the agent group from <c>$global:m365groupId</c> when a step of this
/// session set it, and looks the configured group up by name only when it is empty. The server
/// process keeps one PowerShell session, so the value can come from a group that was created for
/// another name or another configuration, and the call queue would get the wrong agents.
/// These lines run in front of the unchanged script: they keep the value only when it is the ID
/// of the group the configuration names.
/// </summary>
internal static class AgentGroupCheck
{
    public static string InFrontOf(string callQueueScript, string groupName)
        => string.Join("\n", new[]
        {
            "# PhoneDesk MCP: use the group ID of this session only when it is the configured agent group.",
            "if ($global:m365groupId) {",
            "    $pdSessionGroup = $null",
            "    try { $pdSessionGroup = Get-MgGroup -GroupId $global:m365groupId -ErrorAction Stop } catch { $pdSessionGroup = $null }",
            "    if (-not $pdSessionGroup -or $pdSessionGroup.DisplayName -ne '" + SingleQuoted(groupName) + "') {",
            "        $global:m365groupId = $null",
            "    }",
            "}",
            string.Empty,
            string.Empty
        }) + callQueueScript;

    /// <summary>
    /// Escapes a text for a single-quoted PowerShell string. PowerShell also ends such a string at
    /// the typographic quotes, so they are doubled like the plain one.
    /// </summary>
    internal static string SingleQuoted(string value)
        => string.Concat((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Select(character => character is '\'' or '‘' or '’' or '‚' or '‛'
                ? new string(character, 2)
                : character.ToString()));
}
