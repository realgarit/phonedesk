using Xunit;
using PhoneDesk.Services;

namespace PhoneDesk.Tests
{
    public class LoggingServiceTests
    {
        private readonly LoggingService _loggingService;

        public LoggingServiceTests()
        {
            _loggingService = new LoggingService();
        }

        [Fact]
        public void Log_SetsLatestLogEntry()
        {
            _loggingService.Log("First", LogLevel.Info);
            _loggingService.Log("Second", LogLevel.Warning);

            Assert.Contains("Second", _loggingService.LatestLogEntry);
        }

        [Fact]
        public void Log_FormatsWithTimestampAndLevel()
        {
            _loggingService.Log("Test", LogLevel.Error);

            var entry = _loggingService.LogEntries[0];
            Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\] \[Error\] Test", entry);
        }

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            _loggingService.Log("One", LogLevel.Info);
            _loggingService.Log("Two", LogLevel.Info);
            _loggingService.Clear();

            Assert.Empty(_loggingService.LogEntries);
            Assert.Equal(string.Empty, _loggingService.LatestLogEntry);
        }

        [Fact]
        public void Log_SanitizesEmailAddresses()
        {
            _loggingService.Log("User admin@example.com logged in", LogLevel.Info);

            var entry = _loggingService.LogEntries[0];
            Assert.DoesNotContain("admin@example.com", entry);
            Assert.Contains("a***@example.com", entry);
        }

        [Fact]
        public void Log_SanitizesGuids()
        {
            _loggingService.Log("Tenant: 12345678-1234-1234-1234-123456789012", LogLevel.Info);

            var entry = _loggingService.LogEntries[0];
            Assert.DoesNotContain("12345678-1234-1234-1234-123456789012", entry);
        }

        [Fact]
        public void GetFilteredEntries_RespectsMinimumLogLevel()
        {
            _loggingService.Log("Info message", LogLevel.Info);
            _loggingService.Log("Warning message", LogLevel.Warning);
            _loggingService.Log("Error message", LogLevel.Error);

            _loggingService.MinimumLogLevel = LogLevel.Warning;
            var filtered = _loggingService.GetFilteredEntries();

            Assert.Equal(2, filtered.Count);
            Assert.All(filtered, entry =>
                Assert.True(entry.Contains("[Warning]") || entry.Contains("[Error]")));
        }

        [Fact]
        public void Log_SanitizesJwtTokens()
        {
            var fakeToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature_here";
            _loggingService.Log($"Token: {fakeToken}", LogLevel.Info);

            var entry = _loggingService.LogEntries[0];
            Assert.DoesNotContain("eyJ", entry);
            Assert.Contains("[TOKEN REDACTED]", entry);
        }
    }
}
