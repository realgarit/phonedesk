using System;
using System.Collections.Generic;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Services
{
    /// <summary>
    /// Parses the marker-line output of the read-only dashboard query into a <see cref="TenantTopology"/>.
    /// Mirrors the existing "Write-Host PREFIX: a|b|c" convention used across the ScriptBuilders, so the
    /// parsing is tolerant of extra streams (SUCCESS/INFO/ERROR lines) interleaved by PowerShell.
    ///
    /// Line formats (pipe-delimited fields; CSV sub-fields comma-delimited):
    ///   TOPRA:  DisplayName|UserPrincipalName|ObjectId|PhoneNumber|Kind|AccountEnabled
    ///   TOPAA:  Name|Identity|LanguageId|TimeZoneId|ResourceAccountIds|HolidayScheduleIds|CallTargetIds
    ///   TOPCQ:  Name|Identity|RoutingMethod|AgentAlertTime|AgentIds|DistributionListIds|ResourceAccountIds
    ///   TOPGRP: DisplayName|Id|MailNickname|Description
    /// </summary>
    public sealed class TenantTopologyAssembler : ITenantTopologyAssembler
    {
        private const string ResourceAccountPrefix = "TOPRA:";
        private const string AutoAttendantPrefix = "TOPAA:";
        private const string CallQueuePrefix = "TOPCQ:";
        private const string GroupPrefix = "TOPGRP:";

        public TenantTopology Assemble(string? rawOutput, DateTimeOffset retrievedAtUtc)
        {
            var autoAttendants = new List<TopologyAutoAttendant>();
            var callQueues = new List<TopologyCallQueue>();
            var resourceAccounts = new List<TopologyResourceAccount>();
            var groups = new List<TopologyGroup>();

            if (!string.IsNullOrEmpty(rawOutput))
            {
                var lines = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.StartsWith(ResourceAccountPrefix, StringComparison.Ordinal))
                    {
                        var ra = ParseResourceAccount(Payload(line, ResourceAccountPrefix));
                        if (ra is not null)
                        {
                            resourceAccounts.Add(ra);
                        }
                    }
                    else if (line.StartsWith(AutoAttendantPrefix, StringComparison.Ordinal))
                    {
                        var aa = ParseAutoAttendant(Payload(line, AutoAttendantPrefix));
                        if (aa is not null)
                        {
                            autoAttendants.Add(aa);
                        }
                    }
                    else if (line.StartsWith(CallQueuePrefix, StringComparison.Ordinal))
                    {
                        var cq = ParseCallQueue(Payload(line, CallQueuePrefix));
                        if (cq is not null)
                        {
                            callQueues.Add(cq);
                        }
                    }
                    else if (line.StartsWith(GroupPrefix, StringComparison.Ordinal))
                    {
                        var g = ParseGroup(Payload(line, GroupPrefix));
                        if (g is not null)
                        {
                            groups.Add(g);
                        }
                    }
                }
            }

            var orphans = TopologyOrphanDetector.Detect(autoAttendants, callQueues, resourceAccounts);
            return new TenantTopology(autoAttendants, callQueues, resourceAccounts, groups, orphans, retrievedAtUtc);
        }

        private static string Payload(string line, string prefix) => line.Substring(prefix.Length).Trim();

        private static TopologyResourceAccount? ParseResourceAccount(string payload)
        {
            var f = payload.Split('|');
            if (f.Length < 6)
            {
                return null;
            }

            return new TopologyResourceAccount(
                f[0].Trim(),
                f[1].Trim(),
                f[2].Trim(),
                NullIfEmpty(f[3].Trim()),
                ParseKind(f[4].Trim()),
                ParseBool(f[5].Trim(), defaultValue: true));
        }

        private static TopologyAutoAttendant? ParseAutoAttendant(string payload)
        {
            var f = payload.Split('|');
            if (f.Length < 4)
            {
                return null;
            }

            return new TopologyAutoAttendant(
                f[0].Trim(),
                f[1].Trim(),
                f[2].Trim(),
                f[3].Trim(),
                Csv(f, 4),
                Csv(f, 5),
                Csv(f, 6));
        }

        private static TopologyCallQueue? ParseCallQueue(string payload)
        {
            var f = payload.Split('|');
            if (f.Length < 4)
            {
                return null;
            }

            return new TopologyCallQueue(
                f[0].Trim(),
                f[1].Trim(),
                f[2].Trim(),
                int.TryParse(f[3].Trim(), out var alert) ? alert : 0,
                Csv(f, 4),
                Csv(f, 5),
                Csv(f, 6));
        }

        private static TopologyGroup? ParseGroup(string payload)
        {
            var f = payload.Split('|');
            if (f.Length < 2)
            {
                return null;
            }

            // The line is "DisplayName|Id|MailNickname|Description" and a display name can itself
            // contain the separator. The object ID is the first field after the name that is a GUID,
            // so everything before it is the name and everything after the nickname is the description.
            var idIndex = Array.FindIndex(f, 1, field => Guid.TryParse(field.Trim(), out _));
            if (idIndex < 0)
            {
                idIndex = 1;
            }

            return new TopologyGroup(
                string.Join("|", f[..idIndex]).Trim(),
                f[idIndex].Trim(),
                f.Length > idIndex + 1 ? f[idIndex + 1].Trim() : string.Empty,
                f.Length > idIndex + 2 ? string.Join("|", f[(idIndex + 2)..]).Trim() : string.Empty);
        }

        private static IReadOnlyList<string> Csv(string[] fields, int index)
        {
            if (index >= fields.Length)
            {
                return Array.Empty<string>();
            }

            var value = fields[index].Trim();
            if (value.Length == 0)
            {
                return Array.Empty<string>();
            }

            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        private static ResourceAccountKind ParseKind(string value) => value switch
        {
            "AutoAttendant" => ResourceAccountKind.AutoAttendant,
            "CallQueue" => ResourceAccountKind.CallQueue,
            _ => ResourceAccountKind.Unknown,
        };

        private static bool ParseBool(string value, bool defaultValue)
            => bool.TryParse(value, out var b) ? b : defaultValue;

        private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    }
}
