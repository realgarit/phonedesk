namespace PhoneDesk.Services
{
    public static class ConstantsService
    {
        public static class TeamsPhone
        {
            public const string SkuId = "440eaaa8-b3e0-484b-a8be-62870b9ba70a";
            public const string CallQueueAppId = "11cd3e2e-fccb-42ad-ad00-878b93575e07";
            public const string AutoAttendantAppId = "ce933385-9390-45d1-9512-c8d228074e07";
        }

        public static class PowerShell
        {
            public const int DefaultWaitTimeSeconds = 30;
            public const int LongWaitTimeSeconds = 120;
            public const int ConnectionCheckTimeoutSeconds = 30;
            public const int ExitCodeError = 1;
        }

        /// <summary>
        /// Defaults for Microsoft Graph/Teams throttling (HTTP 429) handling: retry backoff schedule
        /// and client-side bulk pacing. These are the single obvious home for the resilience knobs;
        /// <c>ThrottleRetryOptions.Default</c> (Application layer) reads them.
        /// </summary>
        public static class Throttling
        {
            /// <summary>Maximum total attempts for an idempotent operation (1 initial + up to 4 retries).</summary>
            public const int MaxRetryAttempts = 5;

            /// <summary>Base delay for exponential backoff (base * 2^(attempt-1)).</summary>
            public const double BaseDelaySeconds = 2.0;

            /// <summary>Ceiling for a single computed backoff wait.</summary>
            public const double MaxDelaySeconds = 60.0;

            /// <summary>Full-jitter fraction of the base delay added to each backoff wait (0..1).</summary>
            public const double JitterFactor = 0.25;

            /// <summary>Ceiling applied to a server-provided <c>Retry-After</c> so a hostile/misparsed value cannot stall the UI indefinitely.</summary>
            public const double MaxRetryAfterSeconds = 120.0;

            /// <summary>Base inter-item delay inserted between items of a bulk loop.</summary>
            public const double BulkInterItemDelaySeconds = 1.0;

            /// <summary>Ceiling for the adaptive inter-item delay after repeated throttling.</summary>
            public const double BulkMaxInterItemDelaySeconds = 30.0;

            /// <summary>Factor the inter-item delay is multiplied by after a throttle event (reduce pace); the reciprocal is used to recover.</summary>
            public const double BulkPaceMultiplierOnThrottle = 2.0;
        }

        public static class Application
        {
            public const string Version = "Version 3.28.0"; // x-release-please-version
            public const string Copyright = "Patrik Lleshaj © 2026. MIT License.";
        }

        public static class Messages
        {
            public const string WaitingMessage = "Please wait while the previous operation is processed by Microsoft";
            public const string LicenseWaitingMessage = "Please wait while the Teams Phone Resource License is being applied";
        }

        public static class CallQueue
        {
            public const int AgentAlertTime = 30;
            public const int OverflowThreshold = 15;
            public const int TimeoutThreshold = 30;
            public const int MinTimeoutThreshold = 30;
        }

        public static class Naming
        {
            public const string ResourceAccountCallQueuePrefix = "racq-";
            public const string ResourceAccountAutoAttendantPrefix = "raaa-";
            public const string M365GroupPrefix = "ttgrp";
            public const string CallQueuePrefix = "cq-";
            public const string AutoAttendantPrefix = "aa-";
            public const string HolidayPrefix = "hd-";
        }

        public static class PowerShellModules
        {
            public const string MicrosoftTeams = "MicrosoftTeams";
            public const string MicrosoftGraph = "Microsoft.Graph";
            public const string MicrosoftGraphAuthentication = "Microsoft.Graph.Authentication";
            public const string MicrosoftGraphUsers = "Microsoft.Graph.Users";
            public const string MicrosoftGraphUsersActions = "Microsoft.Graph.Users.Actions";
            public const string MicrosoftGraphGroups = "Microsoft.Graph.Groups";
            public const string MicrosoftGraphIdentityDirectoryManagement = "Microsoft.Graph.Identity.DirectoryManagement";
        }

        public static class Pages
        {
            public const string Welcome = "Welcome";
            public const string Dashboard = "Dashboard";
            public const string GetStarted = "Get Started";
            public const string Variables = "Variables";
            public const string M365Groups = "M365 Groups";
            public const string CallQueues = "Call Queues";
            public const string AutoAttendants = "Auto Attendants";
            public const string Holidays = "Holidays";
            public const string Documentation = "Documentation";
            public const string Wizard = "Wizard";
            public const string BulkOperations = "BulkOperations";
            public const string History = "History";
        }

        public static class ErrorDialogTitles
        {
            public const string PowerShellError = "PowerShell Error";
            public const string ValidationError = "Validation Error";
            public const string ConnectionError = "Connection Error";
            public const string Error = "Error";
            public const string Confirmation = "Confirmation";
            public const string Success = "Success";
            public const string Information = "Information";
        }
    }
}