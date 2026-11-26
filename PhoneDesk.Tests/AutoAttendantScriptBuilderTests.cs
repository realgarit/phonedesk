using Moq;
using Xunit;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Models;

namespace PhoneDesk.Tests
{
    public class AutoAttendantScriptBuilderTests
    {
        private readonly Mock<IPowerShellSanitizationService> _mockSanitizer;
        private readonly AutoAttendantScriptBuilder _builder;

        public AutoAttendantScriptBuilderTests()
        {
            _mockSanitizer = new Mock<IPowerShellSanitizationService>();
            _mockSanitizer.Setup(s => s.SanitizeString(It.IsAny<string>())).Returns<string>(s => s);
            _mockSanitizer.Setup(s => s.SanitizeSpokenText(It.IsAny<string>())).Returns<string>(s => s);
            _mockSanitizer.Setup(s => s.SanitizeIdentifier(It.IsAny<string>())).Returns<string>(s => s);
            _builder = new AutoAttendantScriptBuilder(_mockSanitizer.Object);
        }

        [Fact]
        public void GetCreateAutoAttendantCommand_NoDuplicateCallHandlingAssociation()
        {
            var variables = new PhoneManagerVariables
            {
                Customer = "acme",
                CustomerGroupName = "sales",
                MsFallbackDomain = "@acme.onmicrosoft.com",
                RaaAnrName = "main",
                LanguageId = "en-US",
                TimeZoneId = "UTC",
                UsageLocation = "US",
                RaaAnr = "+12025551234",
                PhoneNumberType = "DirectRouting"
            };

            var script = _builder.GetCreateAutoAttendantCommand(variables);

            // Count occurrences of the call handling association line
            var associationLine = "New-CsAutoAttendantCallHandlingAssociation";
            var count = 0;
            var index = 0;
            while ((index = script.IndexOf(associationLine, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += associationLine.Length;
            }

            // Should appear exactly once (the duplicate was a bug we fixed)
            Assert.Equal(1, count);
        }

        [Fact]
        public void GetAttachHolidayToAutoAttendantCommand_ContainsAllParts()
        {
            var script = _builder.GetAttachHolidayToAutoAttendantCommand("hd-test", "aa-test", "Holiday greeting");

            Assert.Contains("Get-CsOnlineSchedule", script);
            Assert.Contains("Set-CsAutoAttendant", script);
            Assert.Contains("hd-test", script);
            Assert.Contains("aa-test", script);
            Assert.Contains("Holiday greeting", script);
            _mockSanitizer.Verify(s => s.SanitizeSpokenText("Holiday greeting"), Times.Once());
            _mockSanitizer.Verify(s => s.SanitizeString("Holiday greeting"), Times.Never());
        }
    }
}
