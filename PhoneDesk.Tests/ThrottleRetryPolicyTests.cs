using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Covers the throttle-aware retry policy (issue #62): the backoff schedule, <c>Retry-After</c>
    /// precedence, the max-attempts give-up, the "only throttling, only idempotent" gating, log
    /// visibility, and prompt cancellation during a backoff wait. Delay and jitter are injected so the
    /// schedule is deterministic and the tests never actually sleep.
    /// </summary>
    public class ThrottleRetryPolicyTests
    {
        private static OperationResult<string> Throttled(string raw = "ERROR: Response status code 429 (TooManyRequests)")
            => PowerShellOperationResultMapper.Map(new PowerShellExecutionResult { Output = raw, HadErrors = true });

        private static OperationResult<string> Success(string raw = "SUCCESS: ok")
            => PowerShellOperationResultMapper.Map(new PowerShellExecutionResult { Output = raw });

        private static OperationResult<string> ValidationFailure()
            => PowerShellOperationResultMapper.Map(new PowerShellExecutionResult
            {
                Output = "ERROR: Cannot validate argument on parameter 'Name'. The argument is not valid.",
                HadErrors = true
            });

        private static (Mock<ILoggingService> logger, List<string> lines) CapturingLogger()
        {
            var lines = new List<string>();
            var logger = new Mock<ILoggingService>();
            logger.Setup(l => l.Log(It.IsAny<string>(), It.IsAny<LogLevel>()))
                  .Callback<string, LogLevel>((m, _) => lines.Add(m));
            return (logger, lines);
        }

        private static ThrottleRetryPolicy PolicyWith(
            ILoggingService logger,
            ThrottleRetryOptions options,
            List<TimeSpan> recordedDelays,
            double jitter = 0.0,
            Func<TimeSpan, CancellationToken, Task>? delayOverride = null)
        {
            Func<TimeSpan, CancellationToken, Task> delay = delayOverride ?? ((ts, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                recordedDelays.Add(ts);
                return Task.CompletedTask;
            });
            return new ThrottleRetryPolicy(logger, options, delay, () => jitter);
        }

        private static ThrottleRetryContext Idempotent(string name = "op") => new(name, isIdempotent: true);

        [Fact]
        public async Task Backoff_schedule_is_exponential_from_the_base_delay()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 5, BaseDelaySeconds = 2, MaxDelaySeconds = 60, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            var invocations = 0;
            var result = await policy.ExecuteAsync(_ => { invocations++; return Task.FromResult(Throttled()); }, Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(5, invocations); // 1 initial + 4 retries
            Assert.Equal(new[] { 2.0, 4.0, 8.0, 16.0 }, delays.ConvertAll(d => d.TotalSeconds));
            Assert.Equal(OperationErrorCategory.Throttling, result.Category);
        }

        [Fact]
        public async Task Backoff_is_capped_at_the_configured_maximum()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 6, BaseDelaySeconds = 2, MaxDelaySeconds = 10, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            await policy.ExecuteAsync(_ => Task.FromResult(Throttled()), Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 2.0, 4.0, 8.0, 10.0, 10.0 }, delays.ConvertAll(d => d.TotalSeconds));
        }

        [Fact]
        public async Task Jitter_is_added_on_top_of_the_backoff()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 2, BaseDelaySeconds = 2, MaxDelaySeconds = 60, JitterFactor = 0.25 };
            var policy = PolicyWith(logger.Object, opts, delays, jitter: 1.0); // full jitter -> 1.0 * 0.25 * 2 = 0.5

            await policy.ExecuteAsync(_ => Task.FromResult(Throttled()), Idempotent(), TestContext.Current.CancellationToken);

            Assert.Single(delays);
            Assert.Equal(2.5, delays[0].TotalSeconds, precision: 6);
        }

        [Fact]
        public async Task RetryAfter_takes_precedence_over_the_computed_backoff()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 3, BaseDelaySeconds = 2, MaxRetryAfterSeconds = 120, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            await policy.ExecuteAsync(
                _ => Task.FromResult(Throttled("ERROR: 429 TooManyRequests. Retry-After: 7")),
                Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 7.0, 7.0 }, delays.ConvertAll(d => d.TotalSeconds));
        }

        [Fact]
        public async Task RetryAfter_is_clamped_to_the_ceiling()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 2, MaxRetryAfterSeconds = 120, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            await policy.ExecuteAsync(
                _ => Task.FromResult(Throttled("ERROR: throttled, Retry-After: 9999")),
                Idempotent(), TestContext.Current.CancellationToken);

            Assert.Single(delays);
            Assert.Equal(120.0, delays[0].TotalSeconds);
        }

        [Fact]
        public async Task Non_throttling_failure_is_never_retried()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var policy = PolicyWith(logger.Object, ThrottleRetryOptions.Default, delays);

            var invocations = 0;
            var result = await policy.ExecuteAsync(_ => { invocations++; return Task.FromResult(ValidationFailure()); }, Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(1, invocations);
            Assert.Empty(delays);
            Assert.Equal(OperationErrorCategory.Validation, result.Category);
        }

        [Fact]
        public async Task Success_is_returned_on_the_first_attempt()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var policy = PolicyWith(logger.Object, ThrottleRetryOptions.Default, delays);

            var invocations = 0;
            var result = await policy.ExecuteAsync(_ => { invocations++; return Task.FromResult(Success()); }, Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(1, invocations);
            Assert.Empty(delays);
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task Transient_throttling_recovers_and_returns_the_eventual_success()
        {
            var (logger, _) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 5, BaseDelaySeconds = 1, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            var invocations = 0;
            var result = await policy.ExecuteAsync(_ =>
            {
                invocations++;
                return Task.FromResult(invocations < 3 ? Throttled() : Success());
            }, Idempotent(), TestContext.Current.CancellationToken);

            Assert.Equal(3, invocations);
            Assert.Equal(2, delays.Count);
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task Non_idempotent_operation_is_not_auto_retried_when_throttled()
        {
            var (logger, lines) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var policy = PolicyWith(logger.Object, ThrottleRetryOptions.Default, delays);

            var invocations = 0;
            var result = await policy.ExecuteAsync(
                _ => { invocations++; return Task.FromResult(Throttled()); },
                new ThrottleRetryContext("BulkOperations", isIdempotent: false), TestContext.Current.CancellationToken);

            Assert.Equal(1, invocations);
            Assert.Empty(delays);
            Assert.Equal(OperationErrorCategory.Throttling, result.Category);
            Assert.Contains(lines, l => l.Contains("non-idempotent", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Retries_are_visible_in_the_log_with_attempt_and_wait()
        {
            var (logger, lines) = CapturingLogger();
            var delays = new List<TimeSpan>();
            var opts = new ThrottleRetryOptions { MaxAttempts = 3, BaseDelaySeconds = 8, JitterFactor = 0 };
            var policy = PolicyWith(logger.Object, opts, delays);

            await policy.ExecuteAsync(_ => Task.FromResult(Throttled()), Idempotent("Connect"), TestContext.Current.CancellationToken);

            Assert.Contains(lines, l => l.Contains("retrying in 8s", StringComparison.OrdinalIgnoreCase)
                                        && l.Contains("attempt 2/3", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Cancellation_during_backoff_aborts_promptly_without_a_further_attempt()
        {
            var (logger, _) = CapturingLogger();
            using var cts = new CancellationTokenSource();
            var opts = new ThrottleRetryOptions { MaxAttempts = 5, BaseDelaySeconds = 1, JitterFactor = 0 };

            // The backoff wait cancels the token and reports cancellation, mimicking a user cancel mid-wait.
            Func<TimeSpan, CancellationToken, Task> cancelDuringWait = (_, _) =>
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            };
            var policy = new ThrottleRetryPolicy(logger.Object, opts, cancelDuringWait, () => 0);

            var invocations = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                policy.ExecuteAsync(_ => { invocations++; return Task.FromResult(Throttled()); }, Idempotent(), cts.Token));

            Assert.Equal(1, invocations); // aborted during the first backoff, before the second attempt
        }

        [Fact]
        public async Task Cancellation_before_the_first_attempt_runs_nothing()
        {
            var (logger, _) = CapturingLogger();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var policy = PolicyWith(logger.Object, ThrottleRetryOptions.Default, new List<TimeSpan>());

            var invocations = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                policy.ExecuteAsync(_ => { invocations++; return Task.FromResult(Success()); }, Idempotent(), cts.Token));

            Assert.Equal(0, invocations);
        }
    }
}
