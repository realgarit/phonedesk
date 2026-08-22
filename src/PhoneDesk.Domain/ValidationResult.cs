namespace PhoneDesk.Services
{
    public enum ValidationErrorCode
    {
        Unknown,
        ModulesNotChecked,
        TeamsNotConnected,
        GraphNotConnected,
        CustomerNameRequired,
        CustomerGroupNameRequired,
        MicrosoftFallbackDomainRequired,
        CustomerLegalNameRequired,
        LanguageIdRequired,
        TimeZoneIdRequired,
        UsageLocationRequired,
        ResourceAccountPhoneNumberRequired,
        PhoneNumberTypeRequired,
        DefaultCallFlowGreetingTextRequired,
        DefaultCallFlowAudioFileRequired,
        AfterHoursCallFlowGreetingTextRequired,
        AfterHoursCallFlowAudioFileRequired,
        HolidayNameSuffixRequired,
        HolidayGreetingPromptRequired,
        HolidayDateInPast
    }

    public sealed record ValidationIssue(ValidationErrorCode Code, string Message);

    /// <summary>
    /// Accumulates validation errors. Pure value object — lives in the Domain layer so both
    /// Domain rules and Application/Presentation validators can produce and consume it.
    /// </summary>
    public class ValidationResult
    {
        private readonly List<ValidationIssue> _issues = new();
        private readonly List<string> _errors = new();

        public bool IsValid => _issues.Count == 0;
        public IReadOnlyList<ValidationIssue> Issues => _issues.AsReadOnly();
        public IReadOnlyList<string> Errors => _errors.AsReadOnly();

        public void AddError(string error)
        {
            AddError(ValidationErrorCode.Unknown, error);
        }

        public void AddError(ValidationErrorCode code, string error)
        {
            _issues.Add(new ValidationIssue(code, error));
            _errors.Add(error);
        }

        public string GetErrorMessage()
        {
            return string.Join("\n", _errors);
        }
    }
}
