using Moq;
using Xunit;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests
{
    public class CommonScriptBuilderTests
    {
        private readonly Mock<IPowerShellSanitizationService> _mockSanitizer;

        public CommonScriptBuilderTests()
        {
            _mockSanitizer = new Mock<IPowerShellSanitizationService>();
        }

        [Fact]
        public void GetCheckModulesCommand_ContainsLinuxPath_ReturnsTrue()
        {
            var builder = new CommonScriptBuilder(_mockSanitizer.Object);
            var script = builder.GetCheckModulesCommand();

            Assert.Contains("linux-x64/Modules", script);
            Assert.Contains("../linux-x64/Modules", script);
        }

        [Fact]
        public void GetCommonSetupScript_ContainsLinuxPath_ReturnsTrue()
        {
            var builder = new CommonScriptBuilder(_mockSanitizer.Object);
            var script = builder.GetCommonSetupScript();

            Assert.Contains("linux-x64/Modules", script);
            Assert.Contains("../linux-x64/Modules", script);
        }

        [Fact]
        public void GetConnectTeamsCommand_DisablesWamForHostedPowerShell()
        {
            var builder = new CommonScriptBuilder(_mockSanitizer.Object);
            var script = builder.GetConnectTeamsCommand();

            Assert.Contains("Connect-MicrosoftTeams -DisableWAM -ErrorAction Stop", script);
            Assert.DoesNotContain("Connect-MicrosoftTeams -ErrorAction Stop", script);
        }
    }
}
