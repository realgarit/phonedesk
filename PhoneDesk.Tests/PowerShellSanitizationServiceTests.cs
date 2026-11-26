using Xunit;
using PhoneDesk.Services;

namespace PhoneDesk.Tests
{
    public class PowerShellSanitizationServiceTests
    {
        private readonly PowerShellSanitizationService _sanitizer = new();

        [Fact]
        public void SanitizeString_NormalInput_PassesThrough()
        {
            var result = _sanitizer.SanitizeString("Hello World 123");
            Assert.Equal("Hello World 123", result);
        }

        [Fact]
        public void SanitizeString_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, _sanitizer.SanitizeString(null!));
            Assert.Equal(string.Empty, _sanitizer.SanitizeString(""));
        }

        [Theory]
        [InlineData("Nous sommes fermés aujourd'hui.", "Nous sommes fermés aujourd'hui.")]
        [InlineData("Nous sommes fermés aujourd\u2019hui.", "Nous sommes fermés aujourd\u2019hui.")]
        [InlineData("Müller & Söhne; bitte warten", "Müller & Söhne; bitte warten")]
        [InlineData("say \"hi\" $(Get-Date) `n", "say hi (Get-Date) n")]
        [InlineData("smart \u201Cquote\u201D \uFF04x", "smart quote x")]
        public void SanitizeSpokenText_KeepsApostrophesAndRemovesWhatEndsADoubleQuotedString(string input, string expected)
        {
            Assert.Equal(expected, _sanitizer.SanitizeSpokenText(input));
        }

        [Fact]
        public void SanitizeSpokenText_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, _sanitizer.SanitizeSpokenText(null!));
            Assert.Equal(string.Empty, _sanitizer.SanitizeSpokenText(""));
        }

        [Fact]
        public void SanitizeString_SingleQuotes_AreDoubled()
        {
            var result = _sanitizer.SanitizeString("it's a test");
            Assert.Equal("it''s a test", result);
        }

        [Theory]
        [InlineData("$var", "var")]
        [InlineData("`escape", "escape")]
        [InlineData("cmd1;cmd2", "cmd1cmd2")]
        [InlineData("cmd1|cmd2", "cmd1cmd2")]
        [InlineData("cmd1&cmd2", "cmd1cmd2")]
        [InlineData("a<b>c", "abc")]
        [InlineData("break\"out", "breakout")]
        public void SanitizeString_DangerousChars_AreRemoved(string input, string expected)
        {
            var result = _sanitizer.SanitizeString(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void SanitizeString_InjectionAttempt_IsNeutralized()
        {
            var malicious = "test'; Invoke-Expression 'malicious'; '";
            var result = _sanitizer.SanitizeString(malicious);
            Assert.DoesNotContain(";", result);
            Assert.DoesNotContain("'", result.Replace("''", ""));
        }

        [Fact]
        public void SanitizeString_ControlCharacters_AreRemoved()
        {
            var input = "hello\x00world\x01test";
            var result = _sanitizer.SanitizeString(input);
            Assert.Equal("helloworldtest", result);
        }

        [Fact]
        public void SanitizeString_UnicodeHomoglyphs_AreReplaced()
        {
            // Unicode fullwidth dollar sign
            var input = "test\uFF04variable";
            var result = _sanitizer.SanitizeString(input);
            // The homoglyph $ gets replaced then stripped by the dangerous char removal
            Assert.DoesNotContain("$", result);
            Assert.DoesNotContain("\uFF04", result);
        }

        [Fact]
        public void SanitizeIdentifier_ValidInput_PassesThrough()
        {
            var result = _sanitizer.SanitizeIdentifier("user@domain.com");
            Assert.Equal("user@domain.com", result);
        }

        [Fact]
        public void SanitizeIdentifier_WithSingleQuotes_ThrowsArgumentException()
        {
            // Single quotes are not in the allowed identifier pattern
            Assert.Throws<ArgumentException>(() => _sanitizer.SanitizeIdentifier("O'Brien"));
        }

        [Fact]
        public void SanitizeIdentifier_NullOrWhitespace_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _sanitizer.SanitizeIdentifier(null!));
            Assert.Throws<ArgumentException>(() => _sanitizer.SanitizeIdentifier(""));
            Assert.Throws<ArgumentException>(() => _sanitizer.SanitizeIdentifier("   "));
        }

        [Theory]
        [InlineData("test;injection")]
        [InlineData("test|pipe")]
        [InlineData("test$var")]
        public void SanitizeIdentifier_InvalidChars_ThrowsArgumentException(string input)
        {
            Assert.Throws<ArgumentException>(() => _sanitizer.SanitizeIdentifier(input));
        }

        [Fact]
        public void SanitizeIdentifier_InternationalChars_Allowed()
        {
            var result = _sanitizer.SanitizeIdentifier("François Müller");
            Assert.Equal("François Müller", result);
        }
    }
}
