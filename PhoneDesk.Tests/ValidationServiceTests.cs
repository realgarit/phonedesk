using Moq;
using Xunit;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests
{
    public class ValidationServiceTests
    {
        private readonly ValidationService _validationService;
        private readonly Mock<ISessionManager> _mockSessionManager;

        public ValidationServiceTests()
        {
            _mockSessionManager = new Mock<ISessionManager>();
            _validationService = new ValidationService(_mockSessionManager.Object);
        }

        [Theory]
        [InlineData("user@example.com", true)]
        [InlineData("user.name@domain.co.uk", true)]
        [InlineData("user+tag@example.com", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("notanemail", false)]
        [InlineData("@domain.com", false)]
        [InlineData("user@", false)]
        [InlineData("user..name@domain.com", false)]
        [InlineData(".user@domain.com", false)]
        [InlineData("user.@domain.com", false)]
        public void IsValidEmail_ReturnsExpectedResult(string email, bool expected)
        {
            Assert.Equal(expected, _validationService.IsValidEmail(email));
        }

        [Theory]
        [InlineData("+41441234567", true)]
        [InlineData("+12025551234", true)]
        [InlineData("+442071234567", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData("41441234567", false)]       // Missing +
        [InlineData("+0441234567", false)]        // Country code starts with 0
        [InlineData("+1234", false)]              // Too short
        [InlineData("+12345678901234567", false)] // Too long
        public void IsValidPhoneNumber_ReturnsExpectedResult(string phone, bool expected)
        {
            Assert.Equal(expected, _validationService.IsValidPhoneNumber(phone));
        }

        [Fact]
        public void ValidateVariables_AllFieldsPresent_IsValid()
        {
            var vars = CreateValidVariables();
            var result = _validationService.ValidateVariables(vars);
            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidateVariables_MissingCustomer_ReturnsError()
        {
            var vars = CreateValidVariables();
            vars.Customer = "";
            var result = _validationService.ValidateVariables(vars);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Customer name"));
            Assert.Contains(result.Issues, issue => issue.Code == ValidationErrorCode.CustomerNameRequired);
        }

        [Fact]
        public void ValidateVariables_MissingMultipleFields_ReturnsMultipleErrors()
        {
            var vars = new PhoneManagerVariables();
            var result = _validationService.ValidateVariables(vars);
            Assert.False(result.IsValid);
            Assert.True(result.Errors.Count > 1);
        }

        [Fact]
        public void ValidatePrerequisites_AllConnected_IsValid()
        {
            _mockSessionManager.Setup(s => s.ModulesChecked).Returns(true);
            _mockSessionManager.Setup(s => s.TeamsConnected).Returns(true);
            _mockSessionManager.Setup(s => s.GraphConnected).Returns(true);

            var result = _validationService.ValidatePrerequisites();
            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidatePrerequisites_NotConnected_ReturnsErrors()
        {
            _mockSessionManager.Setup(s => s.ModulesChecked).Returns(false);
            _mockSessionManager.Setup(s => s.TeamsConnected).Returns(false);
            _mockSessionManager.Setup(s => s.GraphConnected).Returns(false);

            var result = _validationService.ValidatePrerequisites();
            Assert.False(result.IsValid);
            Assert.Equal(3, result.Errors.Count);
        }

        [Fact]
        public void ValidateHolidayDate_Today_IsValid()
        {
            var result = _validationService.ValidateHolidayDate(DateTime.Today);
            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidateHolidayDate_Tomorrow_IsValid()
        {
            var result = _validationService.ValidateHolidayDate(DateTime.Today.AddDays(1));
            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidateHolidayDate_Yesterday_IsInvalid()
        {
            var result = _validationService.ValidateHolidayDate(DateTime.Today.AddDays(-1));
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("past"));
        }

        [Fact]
        public void ValidateVariables_TtsGreetingWithoutText_ReturnsError()
        {
            var vars = CreateValidVariables();
            vars.AaDefaultGreetingType = "TextToSpeech";
            vars.AaDefaultGreetingTextToSpeechPrompt = "";
            var result = _validationService.ValidateVariables(vars);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("greeting text"));
        }

        [Fact]
        public void ValidateVariables_AudioGreetingWithoutFileId_ReturnsError()
        {
            var vars = CreateValidVariables();
            vars.AaDefaultGreetingType = "AudioFile";
            vars.AaDefaultGreetingAudioFileId = "";
            var result = _validationService.ValidateVariables(vars);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("audio file"));
        }

        [Fact]
        public void ValidationResult_GetErrorMessage_JoinsErrors()
        {
            var result = new ValidationResult();
            result.AddError("Error 1");
            result.AddError("Error 2");
            var message = result.GetErrorMessage();
            Assert.Contains("Error 1", message);
            Assert.Contains("Error 2", message);
            Assert.Contains("\n", message);
        }

        [Theory]
        [InlineData("@test.onmicrosoft.com", true)]
        [InlineData("test.onmicrosoft.com", true)]
        [InlineData("test", false)]
        [InlineData("test .onmicrosoft.com", false)]
        [InlineData("user@test.onmicrosoft.com", false)]
        [InlineData("test..onmicrosoft.com", false)]
        [InlineData("elektro-weber-partner.onmicrosoft.com", true)]
        [InlineData("contoso.ch", true)]
        [InlineData("tenant!.onmicrosoft.com", false)]
        [InlineData("tenant_.example.com", false)]
        [InlineData("tenant/example.com", false)]
        [InlineData("-tenant.onmicrosoft.com", false)]
        [InlineData("tenant-.onmicrosoft.com", false)]
        [InlineData("tenant.c0m", false)]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.onmicrosoft.com", false)] // 64-char label
        public void ValidateVariables_FallbackDomainFormat(string domain, bool expectedValid)
        {
            var variables = CreateValidVariables();
            variables.MsFallbackDomain = domain;

            var result = _validationService.ValidateVariables(variables);

            Assert.Equal(
                expectedValid,
                !result.Issues.Any(i => i.Code == ValidationErrorCode.MicrosoftFallbackDomainInvalid));
        }

        [Theory]
        [InlineData(8, 17, 0, 0, true)]     // second range unused
        [InlineData(8, 12, 13, 17, true)]
        [InlineData(0, 0, 0, 0, false)]     // Teams rejects 00:00-00:00
        [InlineData(17, 8, 0, 0, false)]
        [InlineData(8, 12, 17, 13, false)]
        public void ValidateVariables_OpeningHours(int start1, int end1, int start2, int end2, bool expectedValid)
        {
            var variables = CreateValidVariables();
            variables.OpeningHours1Start = TimeSpan.FromHours(start1);
            variables.OpeningHours1End = TimeSpan.FromHours(end1);
            variables.OpeningHours2Start = TimeSpan.FromHours(start2);
            variables.OpeningHours2End = TimeSpan.FromHours(end2);

            var result = _validationService.ValidateVariables(variables);

            Assert.Equal(expectedValid, !result.Issues.Any(i => i.Code == ValidationErrorCode.OpeningHoursInvalid));
        }

        [Fact]
        public void ValidateVariables_DayPlanWithoutOpenDays_IsRejected()
        {
            var variables = CreateValidVariables();
            variables.UsePerDaySchedule = true;
            foreach (var day in variables.WeeklySchedule)
            {
                day.IsEnabled = false;
            }

            var result = _validationService.ValidateVariables(variables);

            Assert.Contains(result.Issues, i => i.Code == ValidationErrorCode.OpeningDaysRequired);
        }

        private static PhoneManagerVariables CreateValidVariables()
        {
            return new PhoneManagerVariables
            {
                Customer = "TestCustomer",
                CustomerGroupName = "TestGroup",
                MsFallbackDomain = "@test.onmicrosoft.com",
                CustomerLegalName = "Test Legal Name",
                LanguageId = "en-US",
                TimeZoneId = "UTC",
                UsageLocation = "US",
                RaaAnr = "+12025551234",
                PhoneNumberType = "DirectRouting",
                HolidayNameSuffix = "2024",
                HolidayGreetingPromptDE = "Test greeting"
            };
        }
    }
}
