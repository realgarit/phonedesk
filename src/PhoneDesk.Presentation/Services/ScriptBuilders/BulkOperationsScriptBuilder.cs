using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PhoneDesk.Services.ScriptBuilders
{
    /// <summary>
    /// Builds PowerShell scripts for bulk operations based on CSV data.
    /// Each row in the CSV represents a complete phone system setup.
    /// </summary>
    public class BulkOperationsScriptBuilder
    {
        private readonly BulkSetupScriptBuilder _shared;

        public BulkOperationsScriptBuilder(
            IPowerShellCommandService commands,
            IPowerShellSanitizationService sanitizer)
        {
            _shared = new BulkSetupScriptBuilder(commands, sanitizer);
        }

        /// <summary>
        /// CSV column headers for the bulk import template.
        /// </summary>
        public static readonly string[] CsvHeaders = BulkSetupScriptBuilder.CsvHeaders;

        /// <summary>
        /// Generates a CSV template with headers and an example row.
        /// </summary>
        public string GenerateCsvTemplate() => _shared.GenerateCsvTemplate();

        /// <summary>
        /// Parses CSV content into a list of PhoneManagerVariables.
        /// </summary>
        public List<PhoneManagerVariables> ParseCsv(string csvContent)
        {
            var results = new List<PhoneManagerVariables>();
            foreach (var row in _shared.ParseCsv(csvContent))
            {
                var vars = new PhoneManagerVariables
                {
                    Customer = row.Customer,
                    CustomerGroupName = row.CustomerGroupName,
                    MsFallbackDomain = row.MsFallbackDomain,
                    RaaAnrName = row.RaaAnrName,
                    LanguageId = row.LanguageId,
                    TimeZoneId = row.TimeZoneId,
                    UsageLocation = row.UsageLocation,
                    RaaAnr = row.RaaAnr,
                    PhoneNumberType = row.PhoneNumberType
                };
                if (row.OpeningHours1Start.HasValue) vars.OpeningHours1Start = row.OpeningHours1Start.Value;
                if (row.OpeningHours1End.HasValue) vars.OpeningHours1End = row.OpeningHours1End.Value;
                if (row.OpeningHours2Start.HasValue) vars.OpeningHours2Start = row.OpeningHours2Start.Value;
                if (row.OpeningHours2End.HasValue) vars.OpeningHours2End = row.OpeningHours2End.Value;
                results.Add(vars);
            }

            return results;
        }

        /// <summary>
        /// Generates a combined PowerShell script for all rows.
        /// Each row goes through the full setup: M365 Group → CQ RA → License → CQ → AA RA → License+Phone → AA → Associate.
        /// </summary>
        public string GenerateBulkScript(List<PhoneManagerVariables> entries)
            => _shared.GenerateBulkScript(entries);
    }
}
