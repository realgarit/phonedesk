using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Pins the generated bulk-operations PowerShell script. Proves that routing
    /// BulkOperationsScriptBuilder through the IPowerShellCommandService facade (instead of the
    /// concrete script builders) leaves the emitted script byte-for-byte identical. The hash was
    /// captured from the pre-refactor implementation.
    /// </summary>
    public class BulkOperationsSnapshotTests
    {
        // Re-captured when the shared resource account, license and M365 group scripts gained
        // -ErrorAction Stop and reuse of existing objects (failed cmdlets no longer print SUCCESS), and the
        // after-hours schedule stopped creating an unused 00:00-00:00 time range, and running the call queue
        // and auto attendant steps again updates existing objects.
        private const string ExpectedBulkScriptHash =
            "FCD9473BF5F207E49971358E5A75C4E9C481BE7AF37E78D5AB760ACDE9B85415";

        private static IPowerShellSanitizationService VerbatimSanitizer()
        {
            var mock = new Mock<IPowerShellSanitizationService>();
            mock.Setup(s => s.SanitizeString(It.IsAny<string>())).Returns<string>(x => x);
            mock.Setup(s => s.SanitizeIdentifier(It.IsAny<string>())).Returns<string>(x => x);
            return mock.Object;
        }

        private static List<PhoneManagerVariables> SampleEntries() => new()
        {
            new PhoneManagerVariables
            {
                Customer = "contoso",
                CustomerGroupName = "hauptnummer",
                MsFallbackDomain = "@contoso.onmicrosoft.com",
                RaaAnrName = "haupt",
                LanguageId = "de-DE",
                TimeZoneId = "W. Europe Standard Time",
                UsageLocation = "CH",
                RaaAnr = "+41441234567",
                PhoneNumberType = "DirectRouting",
            },
        };

        [Fact]
        public void Bulk_script_is_byte_for_byte_unchanged_after_facade_refactor()
        {
            var san = VerbatimSanitizer();
            var facade = new PowerShellCommandService(
                new CommonScriptBuilder(san),
                new CallQueueScriptBuilder(san),
                new AutoAttendantScriptBuilder(san),
                new HolidayScriptBuilder(san),
                new ResourceAccountScriptBuilder(san),
                new DashboardScriptBuilder());

            var bulk = new BulkOperationsScriptBuilder(facade, san);

            var script = bulk.GenerateBulkScript(SampleEntries());

            // Normalize line endings: the builders use StringBuilder.AppendLine (Environment.NewLine),
            // which is CRLF on Windows and LF elsewhere. Hash the LF-normalized content so the snapshot
            // is platform-independent while still catching any real content change. The expected hash
            // was captured from LF output.
            var normalized = script.Replace("\r\n", "\n");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

            Assert.Equal(ExpectedBulkScriptHash, hash);
        }
    }
}
