using System;
using System.Collections.Generic;
using System.Linq;
using PhoneDesk.Models;
using PhoneDesk.Planning;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Planning
{
    /// <summary>
    /// Produces a <see cref="DryRunPlan"/> from configuration inputs without touching the tenant or the
    /// PowerShell/Graph layers. The enumerated objects mirror, as descriptive metadata only, the sequence
    /// of operations the wizard and bulk script builders perform for one setup:
    /// M365 Group → CQ Resource Account → CQ License → Call Queue → AA Resource Account →
    /// AA License + Phone Number → Auto Attendant → Association. It never generates or inspects script text,
    /// so it cannot influence what the frozen builders emit at execution time.
    /// </summary>
    public class DryRunPlanBuilder : IDryRunPlanBuilder
    {
        private readonly IValidationService _validationService;

        public DryRunPlanBuilder(IValidationService validationService)
        {
            _validationService = validationService;
        }

        public DryRunPlan BuildWizardPlan(IPhoneManagerVariables variables)
        {
            var entry = BuildEntry(variables, rowNumber: 1);
            var entries = new List<DryRunEntry> { entry };
            ApplyCrossEntryPreflight(entries, new[] { variables });
            return new DryRunPlan("Setup Wizard", DateTimeOffset.UtcNow, entries);
        }

        public DryRunPlan BuildBulkPlan(IReadOnlyList<IPhoneManagerVariables> entries)
        {
            var built = new List<DryRunEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                built.Add(BuildEntry(entries[i], rowNumber: i + 1));
            }
            ApplyCrossEntryPreflight(built, entries);
            return new DryRunPlan("Bulk Operations (CSV)", DateTimeOffset.UtcNow, built);
        }

        private DryRunEntry BuildEntry(IPhoneManagerVariables v, int rowNumber)
        {
            var label = BuildLabel(v);
            var objects = BuildObjects(v);
            var errors = ValidateForCreateFlow(v);

            // Per-entry preflight is populated by ApplyCrossEntryPreflight after all entries exist, since the
            // only checks that do not require live-tenant access are intra-plan duplicate detections.
            return new DryRunEntry(rowNumber, label, objects, errors, Array.Empty<PreflightCheck>());
        }

        private static string BuildLabel(IPhoneManagerVariables v)
        {
            var customer = string.IsNullOrWhiteSpace(v.Customer) ? "(no customer)" : v.Customer;
            var group = string.IsNullOrWhiteSpace(v.CustomerGroupName) ? "(no group)" : v.CustomerGroupName;
            return $"{customer} - {group}";
        }

        private static IReadOnlyList<PlannedObject> BuildObjects(IPhoneManagerVariables v)
        {
            var objects = new List<PlannedObject>
            {
                new(PlannedObjectType.M365Group, PlannedAction.Create, v.M365Group, null,
                    new List<PlannedSetting>
                    {
                        new("Name", v.M365Group)
                    }),

                new(PlannedObjectType.ResourceAccount, PlannedAction.Create, v.RacqDisplayName, v.RacqUPN,
                    new List<PlannedSetting>
                    {
                        new("Display Name", v.RacqDisplayName),
                        new("UPN", v.RacqUPN),
                        new("Purpose", "Call Queue")
                    }),

                new(PlannedObjectType.License, PlannedAction.Assign, v.RacqDisplayName, v.RacqUPN,
                    new List<PlannedSetting>
                    {
                        new("Usage Location", v.UsageLocation),
                        new("SKU", v.SkuId)
                    }),

                new(PlannedObjectType.CallQueue, PlannedAction.Create, v.CqDisplayName, null,
                    new List<PlannedSetting>
                    {
                        new("Display Name", v.CqDisplayName),
                        new("Language", v.LanguageId)
                    }),

                new(PlannedObjectType.ResourceAccount, PlannedAction.Create, v.RaaaDisplayName, v.RaaaUPN,
                    new List<PlannedSetting>
                    {
                        new("Display Name", v.RaaaDisplayName),
                        new("UPN", v.RaaaUPN),
                        new("Purpose", "Auto Attendant")
                    }),

                new(PlannedObjectType.License, PlannedAction.Assign, v.RaaaDisplayName, v.RaaaUPN,
                    new List<PlannedSetting>
                    {
                        new("Usage Location", v.UsageLocation),
                        new("SKU", v.SkuId)
                    })
            };

            if (!string.IsNullOrWhiteSpace(v.RaaAnr))
            {
                objects.Add(new PlannedObject(PlannedObjectType.PhoneNumber, PlannedAction.Assign, v.RaaaDisplayName, v.RaaaUPN,
                    new List<PlannedSetting>
                    {
                        new("Phone Number", v.RaaAnr),
                        new("Number Type", v.PhoneNumberType)
                    }));
            }

            objects.Add(new PlannedObject(PlannedObjectType.AutoAttendant, PlannedAction.Create, v.AaDisplayName, null,
                new List<PlannedSetting>
                {
                    new("Display Name", v.AaDisplayName),
                    new("Language", v.LanguageId),
                    new("Time Zone", v.TimeZoneId)
                }));

            objects.Add(new PlannedObject(PlannedObjectType.Association, PlannedAction.Associate, v.AaDisplayName, v.RaaaUPN,
                new List<PlannedSetting>
                {
                    new("Resource Account", v.RaaaUPN),
                    new("Auto Attendant", v.AaDisplayName)
                }));

            return objects;
        }

        /// <summary>
        /// Validates the fields the create flow actually consumes. This deliberately does not reuse
        /// <see cref="IValidationService.ValidateVariables"/>, which additionally requires holiday and
        /// greeting-prompt fields that belong to later, separate pages and are not set during wizard/bulk
        /// creation — reusing it would flag every valid creation entry as invalid.
        /// </summary>
        private IReadOnlyList<string> ValidateForCreateFlow(IPhoneManagerVariables v)
        {
            var errors = new List<string>();

            void Require(string value, string field)
            {
                if (string.IsNullOrWhiteSpace(value))
                    errors.Add($"{field} is required.");
            }

            Require(v.Customer, "Customer");
            Require(v.CustomerGroupName, "Customer group name");
            Require(v.MsFallbackDomain, "Microsoft fallback domain");
            Require(v.LanguageId, "Language ID");
            Require(v.TimeZoneId, "Time zone ID");
            Require(v.UsageLocation, "Usage location");
            Require(v.PhoneNumberType, "Phone number type");

            if (string.IsNullOrWhiteSpace(v.RaaAnr))
            {
                errors.Add("Resource account phone number is required.");
            }
            else if (!_validationService.IsValidPhoneNumber(v.RaaAnr))
            {
                errors.Add($"Phone number '{v.RaaAnr}' is not a valid E.164 number.");
            }

            return errors;
        }

        /// <summary>
        /// Appends preflight checks that can be evaluated without live-tenant access: intra-plan duplicate
        /// detection (two entries resolving to the same group, UPN, or phone number would collide on
        /// creation). Live-tenant collision/license/number-availability checks are reported as
        /// <see cref="PreflightStatus.NotChecked"/> so the preview never claims a guarantee it did not verify.
        /// </summary>
        private static void ApplyCrossEntryPreflight(List<DryRunEntry> built, IReadOnlyList<IPhoneManagerVariables> sources)
        {
            var groupCounts = CountOccurrences(sources.Select(s => s.M365Group));
            var racqCounts = CountOccurrences(sources.Select(s => s.RacqUPN));
            var raaaCounts = CountOccurrences(sources.Select(s => s.RaaaUPN));
            var numberCounts = CountOccurrences(sources.Select(s => s.RaaAnr));

            for (int i = 0; i < built.Count; i++)
            {
                var v = sources[i];
                var checks = new List<PreflightCheck>();

                AddDuplicateCheck(checks, "M365 Group name unique in plan", v.M365Group, groupCounts);
                AddDuplicateCheck(checks, "CQ resource account UPN unique in plan", v.RacqUPN, racqCounts);
                AddDuplicateCheck(checks, "AA resource account UPN unique in plan", v.RaaaUPN, raaaCounts);
                AddDuplicateCheck(checks, "Phone number unique in plan", v.RaaAnr, numberCounts);

                checks.Add(new PreflightCheck(
                    PhoneDesk.Operations.TenantPreflight.NotCheckedName,
                    PreflightStatus.NotChecked,
                    "The tenant was not read for this preview. Run the tenant check to compare the plan with what the tenant holds."));

                built[i] = built[i] with { PreflightChecks = checks };
            }
        }

        private static void AddDuplicateCheck(List<PreflightCheck> checks, string name, string value, IReadOnlyDictionary<string, int> counts)
        {
            if (string.IsNullOrWhiteSpace(value))
                return; // Missing values are already reported by validation; don't double-count as a collision.

            if (counts.TryGetValue(value, out var count) && count > 1)
            {
                checks.Add(new PreflightCheck(name, PreflightStatus.Fail,
                    $"'{value}' appears in {count} entries; each must be unique."));
            }
            else
            {
                checks.Add(new PreflightCheck(name, PreflightStatus.Pass, $"'{value}' is unique within the plan."));
            }
        }

        private static IReadOnlyDictionary<string, int> CountOccurrences(IEnumerable<string> values)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                counts[value] = counts.TryGetValue(value, out var c) ? c + 1 : 1;
            }
            return counts;
        }
    }
}
