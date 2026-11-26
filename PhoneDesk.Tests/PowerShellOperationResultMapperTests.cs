using System.Collections.Generic;
using Xunit;
using PhoneDesk.Services;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Covers the mapping layer that turns raw PowerShell output into a typed <see cref="OperationResult{T}"/>:
    /// success, each error category, and malformed / ambiguous output. This is the seam that replaces
    /// string-sniffing (<c>Contains("ERROR:")</c>) in the Presentation layer.
    /// </summary>
    public class PowerShellOperationResultMapperTests
    {
        private static PowerShellExecutionResult Exec(string output, bool hadErrors = false, IReadOnlyList<PowerShellErrorInfo>? errors = null)
            => new()
            {
                Output = output,
                HadErrors = hadErrors,
                Errors = errors ?? System.Array.Empty<PowerShellErrorInfo>()
            };

        [Fact]
        public void Map_SuccessOutput_IsSuccessAndNoErrorCategory()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("SUCCESS: Connected to Microsoft Teams"));

            Assert.True(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.None, result.Category);
            Assert.True(result.HasSuccessMarker);
            Assert.False(result.HasErrorMarker);
            Assert.False(result.ShouldReportError);
            Assert.True(result.Succeeded);
            Assert.Null(result.ErrorMessage);
            Assert.Empty(result.Errors);
            Assert.Equal("SUCCESS: Connected to Microsoft Teams", result.Value);
        }

        [Fact]
        public void Map_ErrorCaughtInsideTheScript_WithSuccess_IsSucceeded()
        {
            // Live tenant behaviour: Get-CsOnlineApplicationInstance throws "not found" inside the script's
            // own try/catch before the create. PowerShell sets HadErrors, but the error stream is empty.
            var result = PowerShellOperationResultMapper.Map(Exec(
                "SUCCESS: Resource account racq-x@contoso.onmicrosoft.com created successfully",
                hadErrors: true));

            Assert.True(result.IsSuccess);
            Assert.True(result.Succeeded);
            Assert.False(result.ShouldReportError);
        }

        [Fact]
        public void Map_HadErrorsWithoutRecordsOrSuccess_IsFailure()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("INFO: nothing reported", hadErrors: true));

            Assert.False(result.IsSuccess);
            Assert.True(result.ShouldReportError);
        }

        [Fact]
        public void Map_SuccessLineFollowedByErrorRecord_IsNotSucceeded()
        {
            // A non-terminating cmdlet error does not stop the script, so the script still prints its own
            // SUCCESS line; the host then appends the error record. That must count as a failure.
            var result = PowerShellOperationResultMapper.Map(Exec(
                "SUCCESS: Resource account created successfully\nERROR: Length cannot be less than zero.",
                hadErrors: true,
                errors: new[] { new PowerShellErrorInfo { Message = "Length cannot be less than zero." } }));

            Assert.False(result.IsSuccess);
            Assert.False(result.Succeeded);
            Assert.True(result.ShouldReportError);
            Assert.Equal("Length cannot be less than zero.", result.ErrorMessage);
        }

        [Fact]
        public void Map_OutputWithoutMarkers_IsTreatedAsSuccess()
        {
            // Data-retrieval commands (e.g. GROUP:/DOCDATA rows) carry no SUCCESS/ERROR marker.
            var result = PowerShellOperationResultMapper.Map(Exec("GROUP: Sales|id-1|sales|desc"));

            Assert.True(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.None, result.Category);
            Assert.False(result.ShouldReportError);
        }

        [Fact]
        public void Map_AuthSessionError_IsCategorizedAsAuthSession()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: Session expired. Please reconnect."));

            Assert.False(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.AuthSession, result.Category);
            Assert.True(result.HasErrorMarker);
            Assert.True(result.ShouldReportError);
        }

        [Fact]
        public void Map_UnauthorizedError_IsCategorizedAsAuthSession()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: The request failed: Unauthorized (401)"));
            Assert.Equal(OperationErrorCategory.AuthSession, result.Category);
        }

        [Fact]
        public void Map_ThrottlingError_IsCategorizedAsThrottling()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: Response status code 429 (TooManyRequests)"));

            Assert.False(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.Throttling, result.Category);
        }

        [Fact]
        public void Map_NotFoundError_IsCategorizedAsNotFound()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: Auto attendant 'aa-x' not found"));

            Assert.Equal(OperationErrorCategory.NotFound, result.Category);
        }

        [Fact]
        public void Map_ValidationError_IsCategorizedAsValidation()
        {
            var result = PowerShellOperationResultMapper.Map(
                Exec("ERROR: Cannot validate argument on parameter 'Name'. The argument is not valid."));

            Assert.Equal(OperationErrorCategory.Validation, result.Category);
        }

        [Fact]
        public void Map_UnrecognizedError_IsCategorizedAsUnknown()
        {
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: Something inexplicable happened"));

            Assert.False(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.Unknown, result.Category);
        }

        [Fact]
        public void Map_MixedSuccessAndError_IsReportedAndNotSucceeded()
        {
            // A batch (e.g. bulk operations) can emit both SUCCESS and ERROR lines. Part of it failed,
            // so the operator must see the error and the operation does not count as succeeded.
            var result = PowerShellOperationResultMapper.Map(
                Exec("SUCCESS: Created call queue\nERROR: Failed to assign license: 429"));

            Assert.True(result.HasSuccessMarker);
            Assert.True(result.HasErrorMarker);
            Assert.True(result.ShouldReportError);
            Assert.False(result.Succeeded);
            // A category is still assigned because an error signal was present.
            Assert.Equal(OperationErrorCategory.Throttling, result.Category);
        }

        [Fact]
        public void Map_StructuredErrorRecords_AreSurfacedAndCategorized()
        {
            var errors = new List<PowerShellErrorInfo>
            {
                new()
                {
                    ExceptionType = "System.Management.Automation.RuntimeException",
                    Message = "Resource account not found",
                    FailingCommand = "Get-CsOnlineApplicationInstance",
                    CategoryInfo = "ObjectNotFound: (:) [], RuntimeException",
                    RawText = "Get-CsOnlineApplicationInstance : Resource account not found"
                }
            };
            var result = PowerShellOperationResultMapper.Map(Exec("ERROR: failed", hadErrors: true, errors: errors));

            Assert.False(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.NotFound, result.Category);
            Assert.Single(result.Errors);
            Assert.Equal("Get-CsOnlineApplicationInstance", result.Errors[0].FailingCommand);
            Assert.Equal("Resource account not found", result.ErrorMessage);
        }

        [Fact]
        public void Map_HadErrorsWithoutMarker_IsStillAFailure()
        {
            var result = PowerShellOperationResultMapper.Map(Exec(string.Empty, hadErrors: true));

            Assert.False(result.IsSuccess);
            Assert.NotEqual(OperationErrorCategory.None, result.Category);
        }

        [Fact]
        public void Map_MalformedEmptyOutput_IsSuccessWithNoErrors()
        {
            var result = PowerShellOperationResultMapper.Map(Exec(string.Empty));

            Assert.True(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.None, result.Category);
            Assert.Equal(string.Empty, result.Value);
        }

        [Fact]
        public void Map_AssignsCorrelationId()
        {
            var a = PowerShellOperationResultMapper.Map(Exec("SUCCESS: ok"));
            var b = PowerShellOperationResultMapper.Map(Exec("SUCCESS: ok"));

            Assert.False(string.IsNullOrWhiteSpace(a.CorrelationId));
            Assert.NotEqual(a.CorrelationId, b.CorrelationId);
        }

        [Fact]
        public void Failure_BuildsTypedFailureWithoutRoundTrip()
        {
            var result = PowerShellOperationResultMapper.Failure(
                OperationErrorCategory.AuthSession,
                "Session expired. Please reconnect.",
                "ERROR: Session expired. Please reconnect.");

            Assert.False(result.IsSuccess);
            Assert.Equal(OperationErrorCategory.AuthSession, result.Category);
            Assert.True(result.HasErrorMarker);
            Assert.True(result.ShouldReportError);
            Assert.Equal("Session expired. Please reconnect.", result.ErrorMessage);
        }

        [Theory]
        [InlineData("ERROR: throttled, retry after 30s", OperationErrorCategory.Throttling)]
        [InlineData("ERROR: AADSTS700082 the token has expired", OperationErrorCategory.AuthSession)]
        [InlineData("ERROR: The group does not exist", OperationErrorCategory.NotFound)]
        [InlineData("ERROR: Parameter binding failed: value is not valid", OperationErrorCategory.Validation)]
        public void Categorize_PrioritizesCorrectly(string output, OperationErrorCategory expected)
        {
            var result = PowerShellOperationResultMapper.Map(Exec(output));
            Assert.Equal(expected, result.Category);
        }
    }
}
