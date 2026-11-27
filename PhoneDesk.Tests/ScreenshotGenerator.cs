using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Automation;
using PhoneDesk.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk;
using PhoneDesk.Localization;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.ViewModels;
using PhoneDesk.Portability;
using PhoneDesk.HealthChecks;
using PhoneDesk.Operations;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Off-screen README screenshot generator. Renders the real app views with the Avalonia
    /// HEADLESS platform (real Skia via the compositor, no on-screen window, no OS window chrome)
    /// and composites a synthetic macOS title bar so the output matches the existing 2560x1496
    /// (2x of 1280x748) screenshots pixel-for-pixel in size.
    ///
    /// This is a development tool, not a CI test: the [Fact] is a no-op unless the environment
    /// variable GENERATE_SCREENSHOTS=1 is set, so CI never initialises Avalonia/Skia here. Run it
    /// locally with:
    ///   GENERATE_SCREENSHOTS=1 SCREENSHOT_OUT=/abs/dir \
    ///     dotnet test phonedesk.Tests/phonedesk.Tests.csproj \
    ///       --filter "FullyQualifiedName~ScreenshotGenerator"
    ///
    /// Services that touch PowerShell / MSAL-Graph / the network are replaced with inert stubs
    /// (see ReplaceSingleton below); the pure string-building command service and script builders
    /// stay real so the wizard "Review Configuration" preview renders authentic text.
    /// </summary>
    public sealed class ScreenshotGenerator
    {
        // Logical client size of the app window; the originals are this at 2x DPI.
        private static readonly int LogicalWidth = int.TryParse(Environment.GetEnvironmentVariable("SCREENSHOT_WIDTH"), out var width) ? Math.Max(1024, width) : 1280;
        private static readonly int LogicalHeight = int.TryParse(Environment.GetEnvironmentVariable("SCREENSHOT_HEIGHT"), out var height) ? Math.Max(640, height) : 720;
        private const double Scale = 2.0;
        private static readonly int PixelWidth = (int)(LogicalWidth * Scale);          // 2560
        private static readonly int ContentPixelHeight = (int)(LogicalHeight * Scale); // 1440
        private const int TitleBarPixelHeight = 56;                          // 28pt @2x
        private const int MaxAttempts = 3;

        // SCREENSHOT_FRAMELESS=1 skips the synthetic macOS title bar (2560x1440 output) —
        // used for platform-neutral shots, e.g. the Microsoft Store listing.
        private static bool Frameless =>
            Environment.GetEnvironmentVariable("SCREENSHOT_FRAMELESS") == "1";

        private static int PixelHeight =>
            Frameless ? ContentPixelHeight : ContentPixelHeight + TitleBarPixelHeight;

        private sealed record Shot(string Page, bool Dark, string FileName, string Scenario = "empty");

        private static readonly Shot[] Shots =
        {
            new("Welcome", true, "navigation-closed-Welcome-en.png", "navigation-closed-Welcome-en"),
            new("Welcome", true, "navigation-closed-Welcome-de.png", "navigation-closed-Welcome-de"),
            new("GetStarted", true, "navigation-closed-GetStarted-en.png", "navigation-closed-GetStarted-en"),
            new("GetStarted", true, "navigation-help-GetStarted-en.png", "navigation-help-GetStarted-en"),
            new("GetStarted", true, "navigation-closed-GetStarted-de.png", "navigation-closed-GetStarted-de"),
            new("GetStarted", true, "navigation-help-GetStarted-de.png", "navigation-help-GetStarted-de"),
            new("Variables", true, "navigation-closed-Variables-en.png", "navigation-closed-Variables-en"),
            new("Variables", true, "navigation-help-Variables-en.png", "navigation-help-Variables-en"),
            new("Variables", true, "navigation-closed-Variables-de.png", "navigation-closed-Variables-de"),
            new("Variables", true, "navigation-help-Variables-de.png", "navigation-help-Variables-de"),
            new("Wizard", true, "navigation-closed-Wizard-en.png", "navigation-closed-Wizard-en"),
            new("Wizard", true, "navigation-closed-Wizard-de.png", "navigation-closed-Wizard-de"),
            new("M365Groups", true, "navigation-closed-M365Groups-en.png", "navigation-closed-M365Groups-en"),
            new("M365Groups", true, "navigation-help-M365Groups-en.png", "navigation-help-M365Groups-en"),
            new("M365Groups", true, "navigation-closed-M365Groups-de.png", "navigation-closed-M365Groups-de"),
            new("M365Groups", true, "navigation-help-M365Groups-de.png", "navigation-help-M365Groups-de"),
            new("CallQueues", true, "navigation-closed-CallQueues-en.png", "navigation-closed-CallQueues-en"),
            new("CallQueues", true, "navigation-help-CallQueues-en.png", "navigation-help-CallQueues-en"),
            new("CallQueues", true, "navigation-closed-CallQueues-de.png", "navigation-closed-CallQueues-de"),
            new("CallQueues", true, "navigation-help-CallQueues-de.png", "navigation-help-CallQueues-de"),
            new("AutoAttendants", true, "navigation-closed-AutoAttendants-en.png", "navigation-closed-AutoAttendants-en"),
            new("AutoAttendants", true, "navigation-help-AutoAttendants-en.png", "navigation-help-AutoAttendants-en"),
            new("AutoAttendants", true, "navigation-closed-AutoAttendants-de.png", "navigation-closed-AutoAttendants-de"),
            new("AutoAttendants", true, "navigation-help-AutoAttendants-de.png", "navigation-help-AutoAttendants-de"),
            new("Holidays", true, "navigation-closed-Holidays-en.png", "navigation-closed-Holidays-en"),
            new("Holidays", true, "navigation-help-Holidays-en.png", "navigation-help-Holidays-en"),
            new("Holidays", true, "navigation-closed-Holidays-de.png", "navigation-closed-Holidays-de"),
            new("Holidays", true, "navigation-help-Holidays-de.png", "navigation-help-Holidays-de"),
            new("Dashboard", true, "navigation-closed-Dashboard-en.png", "navigation-closed-Dashboard-en"),
            new("Dashboard", true, "navigation-help-Dashboard-en.png", "navigation-help-Dashboard-en"),
            new("Dashboard", true, "navigation-closed-Dashboard-de.png", "navigation-closed-Dashboard-de"),
            new("Dashboard", true, "navigation-help-Dashboard-de.png", "navigation-help-Dashboard-de"),
            new("HealthCheck", true, "navigation-closed-HealthCheck-en.png", "navigation-closed-HealthCheck-en"),
            new("HealthCheck", true, "navigation-help-HealthCheck-en.png", "navigation-help-HealthCheck-en"),
            new("HealthCheck", true, "navigation-closed-HealthCheck-de.png", "navigation-closed-HealthCheck-de"),
            new("HealthCheck", true, "navigation-help-HealthCheck-de.png", "navigation-help-HealthCheck-de"),
            new("BulkOperations", true, "navigation-closed-BulkOperations-en.png", "navigation-closed-BulkOperations-en"),
            new("BulkOperations", true, "navigation-help-BulkOperations-en.png", "navigation-help-BulkOperations-en"),
            new("BulkOperations", true, "navigation-closed-BulkOperations-de.png", "navigation-closed-BulkOperations-de"),
            new("BulkOperations", true, "navigation-help-BulkOperations-de.png", "navigation-help-BulkOperations-de"),
            new("Documentation", true, "navigation-closed-Documentation-en.png", "navigation-closed-Documentation-en"),
            new("Documentation", true, "navigation-help-Documentation-en.png", "navigation-help-Documentation-en"),
            new("Documentation", true, "navigation-closed-Documentation-de.png", "navigation-closed-Documentation-de"),
            new("Documentation", true, "navigation-help-Documentation-de.png", "navigation-help-Documentation-de"),
            new("History", true, "navigation-closed-History-en.png", "navigation-closed-History-en"),
            new("History", true, "navigation-help-History-en.png", "navigation-help-History-en"),
            new("History", true, "navigation-closed-History-de.png", "navigation-closed-History-de"),
            new("History", true, "navigation-help-History-de.png", "navigation-help-History-de"),
            new("Welcome", true, "navigation-tour-Welcome-en.png", "navigation-tour-Welcome-en"),
            new("Welcome", true, "navigation-tour-Welcome-de.png", "navigation-tour-Welcome-de"),
            new("GetStarted", true, "navigation-tour-GetStarted-en.png", "navigation-tour-GetStarted-en"),
            new("GetStarted", true, "navigation-tour-GetStarted-de.png", "navigation-tour-GetStarted-de"),
            new("Variables", true, "navigation-tour-Variables-en.png", "navigation-tour-Variables-en"),
            new("Variables", true, "navigation-tour-Variables-de.png", "navigation-tour-Variables-de"),
            new("Wizard", true, "navigation-tour-Wizard-en.png", "navigation-tour-Wizard-en"),
            new("Wizard", true, "navigation-tour-Wizard-de.png", "navigation-tour-Wizard-de"),
            new("GetStarted", true, "navigation-ready-GetStarted-en.png", "navigation-ready-GetStarted-en"),
            new("GetStarted", true, "navigation-ready-GetStarted-de.png", "navigation-ready-GetStarted-de"),
            new("Variables", true, "navigation-ready-Variables-en.png", "navigation-ready-Variables-en"),
            new("Variables", true, "navigation-ready-Variables-de.png", "navigation-ready-Variables-de"),
            new("Variables", false, "navigation-light-help-Variables-en.png", "navigation-help-Variables-en"),
            new("GetStarted", false, "navigation-light-GetStarted-de.png", "navigation-closed-GetStarted-de"),
            new("Welcome", false, "navigation-light-tour-Welcome-en.png", "navigation-tour-Welcome-en"),
            new("Welcome", true, "shell-settings-en.png", "settings-en"),
            new("Welcome", true, "shell-settings-de.png", "settings-de"),
            new("Welcome", true, "welcome.png"),
            new("Welcome", true, "welcome-de.png", "novice-de"),
            new("Welcome", true, "welcome-basics-en.png", "novice-basics-en"),
            new("Welcome", true, "welcome-basics-de.png", "novice-basics-de"),
            new("GetStarted", true, "get-started-checklist-en.png", "novice-checklist-en"),
            new("GetStarted", true, "get-started-checklist-de.png", "novice-checklist-de"),
            new("GetStarted", true, "get-started.png"),
            new("GetStarted", true, "get-started-ready.png", "ready"),
            new("GetStarted", true, "get-started-connections-en.png", "ready-connections-en"),
            new("GetStarted", true, "get-started-connections-de.png", "ready-connections-de"),
            new("GetStarted", true, "get-started-ready-de.png", "ready-de"),
            new("GetStarted", true, "get-started-identity-en.png", "ready-identity-en"),
            new("GetStarted", true, "get-started-identity-de.png", "ready-identity-de"),
            new("GetStarted", true, "get-started-identity-mismatch-en.png", "ready-identitymismatch-en"),
            new("GetStarted", true, "get-started-identity-mismatch-de.png", "ready-identitymismatch-de"),
            new("Variables", true, "holiday-audit-de-by-en.png", "holiday-audit-de-by-en"),
            new("Variables", true, "holiday-audit-de-by-de.png", "holiday-audit-de-by-de"),
            new("Variables", true, "holiday-audit-gb-en.png", "holiday-audit-gb-en"),
            new("Variables", true, "holiday-audit-ics-en.png", "holiday-audit-ics-en"),
            new("Variables", true, "holiday-audit-ics-de.png", "holiday-audit-ics-de"),
            new("Variables", true, "holiday-audit-empty-en.png", "holiday-audit-empty-en"),
            new("Variables", true, "holiday-audit-empty-de.png", "holiday-audit-empty-de"),
            new("Variables", true, "holiday-audit-fr-required-en.png", "holiday-audit-fr-required-en"),
            new("Variables", true, "holiday-audit-fr-required-de.png", "holiday-audit-fr-required-de"),
            new("Variables", true, "holiday-audit-fr-catholic-en.png", "holiday-audit-fr-catholic-en"),
            new("Variables", true, "holiday-audit-fr-catholic-de.png", "holiday-audit-fr-catholic-de"),
            new("Variables", true, "holiday-audit-fr-reformed-en.png", "holiday-audit-fr-reformed-en"),
            new("Variables", true, "holiday-audit-fr-reformed-de.png", "holiday-audit-fr-reformed-de"),
            new("Variables", true, "holiday-audit-so-bucheggberg-en.png", "holiday-audit-so-bucheggberg-en"),
            new("Variables", true, "holiday-audit-so-bucheggberg-de.png", "holiday-audit-so-bucheggberg-de"),
            new("Variables", true, "holiday-audit-so-other-en.png", "holiday-audit-so-other-en"),
            new("Variables", true, "holiday-audit-so-other-de.png", "holiday-audit-so-other-de"),
            new("Variables", true, "holiday-audit-ti-en.png", "holiday-audit-ti-en"),
            new("Variables", true, "holiday-audit-ti-de.png", "holiday-audit-ti-de"),
            new("Variables", true, "holiday-audit-ge-en.png", "holiday-audit-ge-en"),
            new("Variables", true, "holiday-audit-ge-de.png", "holiday-audit-ge-de"),
            new("Variables", true, "holiday-audit-ag-open-en.png", "holiday-audit-ag-open-en"),
            new("Variables", true, "holiday-audit-ag-open-de.png", "holiday-audit-ag-open-de"),
            new("Variables", true, "holiday-audit-manager-en.png", "holiday-audit-manager-en"),
            new("Variables", true, "holiday-audit-manager-de.png", "holiday-audit-manager-de"),
            new("Variables", true, "variables.png"),
            new("Variables", true, "variables-de.png", "ready-de"),
            new("Variables", true, "config-import-preview-en.png", "issue71-config-import-en"),
            new("Variables", true, "config-import-preview-de.png", "issue71-config-import-de"),
            new("Dashboard", true, "dashboard-de.png", "ready-de"),
            new("HolidayManager", true, "holiday-manager-empty-en.png", "holidaymanager-empty-en"),
            new("HolidayManager", true, "holiday-manager-empty-de.png", "holidaymanager-empty-de"),
            new("HolidayManager", true, "holiday-manager-loaded-en.png", "holidaymanager-loaded-en"),
            new("HolidayManager", true, "holiday-manager-loaded-de.png", "holidaymanager-loaded-de"),
            new("HolidayManager", true, "holiday-manager-dates-en.png", "holidaymanager-dates-en"),
            new("HolidayManager", true, "holiday-manager-dates-de.png", "holidaymanager-dates-de"),
            new("HolidayManager", true, "holiday-manager-greeting-en.png", "holidaymanager-greeting-en"),
            new("HolidayManager", true, "holiday-manager-greeting-de.png", "holidaymanager-greeting-de"),
            new("HolidayManager", true, "holiday-manager-attach-en.png", "holidaymanager-attach-en"),
            new("HolidayManager", true, "holiday-manager-attach-de.png", "holidaymanager-attach-de"),
            new("HolidayManager", true, "holiday-manager-retire-en.png", "holidaymanager-retire-en"),
            new("HolidayManager", true, "holiday-manager-retire-de.png", "holidaymanager-retire-de"),
            new("HolidayManager", true, "holiday-manager-compare-en.png", "holidaymanager-compare-en"),
            new("HolidayManager", true, "holiday-manager-compare-de.png", "holidaymanager-compare-de"),
            new("PhoneNumbers", true, "phone-numbers-empty-en.png", "phonenumbers-empty-en"),
            new("PhoneNumbers", true, "phone-numbers-empty-de.png", "phonenumbers-empty-de"),
            new("PhoneNumbers", true, "phone-numbers-loaded-en.png", "phonenumbers-loaded-en"),
            new("PhoneNumbers", true, "phone-numbers-loaded-de.png", "phonenumbers-loaded-de"),
            new("PhoneNumbers", true, "phone-numbers-move-en.png", "phonenumbers-move-en"),
            new("PhoneNumbers", true, "phone-numbers-move-de.png", "phonenumbers-move-de"),
            new("PhoneNumbers", true, "phone-numbers-unassign-en.png", "phonenumbers-unassign-en"),
            new("PhoneNumbers", true, "phone-numbers-unassign-de.png", "phonenumbers-unassign-de"),
            new("HealthCheck", true, "health-check-empty-en.png", "issue78-health-empty-en"),
            new("HealthCheck", true, "health-check-empty-de.png", "issue78-health-empty-de"),
            new("HealthCheck", true, "health-check-findings-en.png", "issue78-health-findings-en"),
            new("HealthCheck", true, "health-check-findings-de.png", "issue78-health-findings-de"),
            new("HealthCheck", true, "health-check-suppressed-en.png", "issue78-health-suppressed-en"),
            new("HealthCheck", true, "health-check-suppressed-de.png", "issue78-health-suppressed-de"),
            new("M365Groups", true, "m365-groups.png"),
            new("M365Groups", true, "task5-m365-groups-empty-en.png", "task5-m365-empty-en"),
            new("M365Groups", true, "task5-m365-groups-empty-de.png", "task5-m365-empty-de"),
            new("CallQueues", true, "call-queues.png"),
            new("CallQueues", true, "task5-call-queues-populated-en.png", "task5-callqueues-populated-en"),
            new("CallQueues", true, "task5-call-queues-populated-de.png", "task5-callqueues-populated-de"),
            new("AutoAttendants", true, "auto-attendants.png"),
            new("AutoAttendants", true, "task5-auto-attendants-filter-en.png", "task5-autoattendants-filter-en"),
            new("AutoAttendants", true, "task5-auto-attendants-filter-de.png", "task5-autoattendants-filter-de"),
            new("Holidays", true, "holidays.png"),
            new("Holidays", true, "task5-holidays-dialog-en.png", "task5-holidays-dialog-en"),
            new("Holidays", true, "task5-holidays-dialog-de.png", "task5-holidays-dialog-de"),
            new("Wizard", true, "setup-wizard.png"),
            new("Wizard", true, "setup-wizard-invalid-de.png", "invalid-de"),
            new("Wizard", true, "setup-wizard-validation-details.png", "validation-details"),
            new("Wizard", true, "setup-wizard-validation-details-de.png", "validation-details-de"),
            new("Wizard", true, "setup-wizard-ready.png", "ready"),
            new("Wizard", true, "setup-wizard-ready-de.png", "ready-de"),
            new("Wizard", true, "setup-wizard-failed.png", "failed"),
            new("Wizard", true, "setup-wizard-failed-de.png", "failed-de"),
            new("Wizard", true, "setup-wizard-step-error.png", "failed-step-error"),
            new("Wizard", true, "setup-wizard-step-error-de.png", "failed-step-error-de"),
            new("Wizard", true, "setup-wizard-progress.png", "ready-progress"),
            new("Wizard", true, "setup-wizard-progress-de.png", "ready-progress-de"),
            new("Wizard", true, "setup-wizard-plan.png", "ready-plan"),
            new("Wizard", true, "setup-wizard-plan-de.png", "ready-plan-de"),
            new("Wizard", true, "setup-wizard-plan-live.png", "ready-plan-live"),
            new("Wizard", true, "setup-wizard-plan-live-de.png", "ready-plan-live-de"),
            new("Wizard", true, "setup-wizard-callqueue-step.png", "ready-callqueue-step"),
            new("Wizard", true, "setup-wizard-callqueue-step-de.png", "ready-callqueue-step-de"),
            new("Wizard", true, "setup-wizard-last-step.png", "ready-last-step"),
            new("Wizard", true, "setup-wizard-last-step-de.png", "ready-last-step-de"),
            new("Variables", true, "config-callqueue-callerid-en.png", "ready-callerid-en"),
            new("Variables", true, "config-callqueue-callerid-de.png", "ready-callerid-de"),
            new("Variables", true, "config-autoattendant-routing-en.png", "ready-routing-en"),
            new("Variables", true, "config-autoattendant-routing-de.png", "ready-routing-de"),
            new("Variables", true, "config-autoattendant-audio-de.png", "ready-routing-audio-de"),
            new("BulkOperations", true, "bulk-operations.png"),
            new("BulkOperations", true, "task5-bulk-operations-error-en.png", "task5-bulk-error-en"),
            new("BulkOperations", true, "task5-bulk-operations-error-de.png", "task5-bulk-error-de"),
            new("Documentation", true, "documentation-empty-en.png", "task6-documentation-empty-en"),
            new("Documentation", true, "documentation-empty-de.png", "task6-documentation-empty-de"),
            new("Documentation", true, "documentation.png"),
            new("Documentation", true, "documentation-de.png", "ready-de"),
            new("Documentation", true, "topology-drift-en.png", "issue71-topology-drift-en"),
            new("Documentation", true, "topology-drift-de.png", "issue71-topology-drift-de"),
            new("History", true, "history-de.png", "ready-de"),
            new("Welcome", true, "task6-update-banner-en.png", "task6-update-banner-en"),
            new("Welcome", true, "task6-update-banner-de.png", "task6-update-banner-de"),
            new("Welcome", true, "task6-script-preview-en.png", "task6-script-preview-en"),
            new("Welcome", true, "task6-script-preview-de.png", "task6-script-preview-de"),
            new("Welcome", true, "task6-confirmation-en.png", "task6-confirmation-en"),
            new("Welcome", true, "task6-confirmation-de.png", "task6-confirmation-de"),
            new("Welcome", true, "task6-error-dialog-en.png", "task6-error-en"),
            new("Welcome", true, "task6-error-dialog-de.png", "task6-error-de"),
            new("Welcome", false, "welcome-light.png"),
        };

        [Fact]
        public void GenerateReadmeScreenshots()
        {
            if (Environment.GetEnvironmentVariable("GENERATE_SCREENSHOTS") != "1")
            {
                // No-op in CI. Only runs when explicitly requested locally.
                return;
            }

            var outDir = Environment.GetEnvironmentVariable("SCREENSHOT_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
            {
                throw new InvalidOperationException("SCREENSHOT_OUT must point at the output directory.");
            }

            Directory.CreateDirectory(outDir);

            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var app = Application.Current
                ?? throw new InvalidOperationException("Avalonia application was not initialised.");

            using var provider = BuildProvider();
            ((App)app).Services = provider;
            app.Resources["TranslationCatalog"] = provider.GetRequiredService<ITranslationService>().Text;

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = LogicalWidth,
                Height = LogicalHeight,
            };
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }
            window.Show();
            ForceRenderScaling(window, Scale);
            PumpRender();

            var navList = window.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(l => l.Name == "NavigationListBox")
                ?? throw new InvalidOperationException("NavigationListBox not found in MainWindow.");

            Assert.Equal("Welcome", (navList.SelectedItem as ListBoxItem)?.Tag);

            var kept = new List<string>();
            string? previousPage = null;

            foreach (var shot in Shots.Where(s => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCREENSHOT_FILTER"))
                || Environment.GetEnvironmentVariable("SCREENSHOT_FILTER")!.Split(',').Any(prefix => s.FileName.StartsWith(prefix, StringComparison.Ordinal))))
            {
                if (shot.Scenario.StartsWith("task6-", StringComparison.Ordinal))
                {
                    using var isolatedProvider = BuildProvider();
                    ((App)app).Services = isolatedProvider;
                    app.Resources["TranslationCatalog"] = isolatedProvider.GetRequiredService<ITranslationService>().Text;

                    using var isolated = CreateShotContext(isolatedProvider);
                    app.RequestedThemeVariant = shot.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    ApplyScenario(isolatedProvider, shot.Scenario);
                    SelectNav(isolated.NavigationList, shot.Page);
                    PumpRender();
                    ApplyPostNavigationScenario(isolatedProvider, isolated.Window, isolated.ViewModel, shot.Scenario);

                    if (!CaptureShot(isolated.Window, shot, outDir, out var isolatedStats))
                    {
                        kept.Add(shot.FileName);
                        Console.WriteLine($"[screenshot] {shot.FileName}: KEPT OLD (validation failed after {MaxAttempts} attempts)");
                        continue;
                    }

                    Console.WriteLine(
                        $"[screenshot] {shot.FileName}: {isolatedStats.Width}x{isolatedStats.Height} " +
                        $"distinctColors={isolatedStats.DistinctColors} brandPurplePixels={isolatedStats.BrandPurplePixels}");
                    continue;
                }

                ((App)app).Services = provider;
                app.Resources["TranslationCatalog"] = provider.GetRequiredService<ITranslationService>().Text;
                app.RequestedThemeVariant = shot.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                if (string.Equals(previousPage, shot.Page, StringComparison.Ordinal))
                {
                    SelectNav(navList, "Welcome");
                    PumpRender();
                }
                ApplyScenario(provider, shot.Scenario);
                // Drive navigation the way a user does — by selecting the sidebar item. This both
                // navigates (via the SelectionChanged handler) and paints the selected-item highlight,
                // which service-only navigation would not do for the already-current page.
                SelectNav(navList, shot.Page);
                PumpRender();
                ApplyPostNavigationScenario(provider, window, vm, shot.Scenario);

                if (!CaptureShot(window, shot, outDir, out var stats))
                {
                    kept.Add(shot.FileName);
                    Console.WriteLine($"[screenshot] {shot.FileName}: KEPT OLD (validation failed after {MaxAttempts} attempts)");
                    continue;
                }

                Console.WriteLine(
                    $"[screenshot] {shot.FileName}: {stats.Width}x{stats.Height} " +
                    $"distinctColors={stats.DistinctColors} brandPurplePixels={stats.BrandPurplePixels}");
                foreach (var help in window.GetVisualDescendants().OfType<InfoButton>())
                {
                    help.Flyout?.Hide();
                    ToolTip.SetIsOpen(help, false);
                }
                previousPage = shot.Page;
            }

            if (kept.Count > 0)
            {
                Console.WriteLine("[screenshot] KEPT-OLD summary: " + string.Join(", ", kept));
            }
        }

        /// <summary>
        /// Renders the shot, composites the title bar, validates the PNG, and only overwrites the
        /// destination when it passes. On repeated validation failure the destination is left
        /// untouched (the old screenshot is kept) and false is returned.
        /// </summary>
        private static bool CaptureShot(MainWindow window, Shot shot, string outDir, out FrameStats stats)
        {
            var finalPath = Path.Combine(outDir, shot.FileName);
            var tempPath = finalPath + ".tmp.png";
            stats = default;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // More render ticks on each retry.
                PumpRender();

                using (var content = CaptureContent(window))
                {
                    Composite(content, shot.Dark, tempPath);
                }

                if (TryValidate(tempPath, out stats))
                {
                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(tempPath, finalPath);
                    return true;
                }
            }

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            return false;
        }

        private static void SelectNav(ListBox navList, string page)
        {
            foreach (var item in navList.Items)
            {
                if (item is ListBoxItem listBoxItem
                    && listBoxItem.Tag is string tag
                    && string.Equals(tag, page, StringComparison.Ordinal))
                {
                    if (ReferenceEquals(navList.SelectedItem, listBoxItem))
                    {
                        navList.SelectedItem = null;
                        PumpRender();
                    }
                    navList.SelectedItem = listBoxItem;
                    return;
                }
            }

            throw new InvalidOperationException($"No sidebar nav item with Tag '{page}'.");
        }

        private static void ApplyScenario(ServiceProvider provider, string scenario)
        {
            var session = provider.GetRequiredService<ISessionManager>();
            var sharedState = provider.GetRequiredService<ISharedStateService>();
            var translation = provider.GetRequiredService<ITranslationService>();

            translation.CurrentLanguage = scenario.EndsWith("-de", StringComparison.Ordinal)
                ? AppLanguage.German
                : AppLanguage.English;

            // Guided Setup progress lives in the shared state; every shot starts without it.
            sharedState.WizardProgress = null;

            if (scenario.StartsWith("ready", StringComparison.Ordinal)
                || scenario.StartsWith("failed", StringComparison.Ordinal)
                || scenario.StartsWith("navigation-ready-", StringComparison.Ordinal))
            {
                sharedState.Variables = new PhoneManagerVariables
                {
                    Customer = "contoso",
                    CustomerGroupName = "reception",
                    MsFallbackDomain = "contoso.onmicrosoft.com",
                    CustomerLegalName = "Contoso AG",
                    LanguageId = "de-DE",
                    TimeZoneId = "W. Europe Standard Time",
                    UsageLocation = "CH",
                    RaaAnr = "+41441234567",
                    PhoneNumberType = "CallingPlan",
                    AaDefaultGreetingType = "TextToSpeech",
                    AaDefaultGreetingTextToSpeechPrompt = "Welcome to Contoso.",
                    AaAfterHoursGreetingType = "TextToSpeech",
                    AaAfterHoursGreetingTextToSpeechPrompt = "Our office is currently closed.",
                    HolidayNameSuffix = "holiday",
                    HolidayGreetingPromptDE = "Unser Büro ist heute geschlossen."
                };
                session.UpdateModulesChecked(true);
                session.UpdateTeamsConnection(true, "operator@contoso.com");
                session.UpdateGraphConnection(true, "operator@contoso.com");
                return;
            }

            sharedState.Variables = new PhoneManagerVariables();
            session.ResetSession();
        }

        private static void ApplyPostNavigationScenario(ServiceProvider provider, MainWindow window, MainWindowViewModel mainWindowViewModel, string scenario)
        {
            if (string.Equals(scenario, "settings-en", StringComparison.Ordinal)
                || string.Equals(scenario, "settings-de", StringComparison.Ordinal))
            {
                mainWindowViewModel.IsSettingsOpen = true;
                return;
            }

            mainWindowViewModel.IsSettingsOpen = false;
            if (scenario.StartsWith("navigation-", StringComparison.Ordinal))
            {
                if (mainWindowViewModel.CurrentViewModel is GetStartedViewModel readiness)
                {
                    var prerequisite = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ReadinessPrerequisite");
                    Assert.Equal(!readiness.CanProceed, prerequisite.IsVisible);
                    Assert.Equal(readiness.SetupGuidanceDetail, prerequisite.Text);
                }
                if (mainWindowViewModel.CurrentViewModel is VariablesViewModel)
                {
                    var next = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContinueToGuidedSetupButton");
                    Assert.NotNull(next.Command);
                    Assert.True(next.Command.CanExecute(null));
                }
                mainWindowViewModel.IsIntroductionVisible = scenario.StartsWith("navigation-tour-", StringComparison.Ordinal)
                    && mainWindowViewModel.CurrentPage != "Wizard";
                PumpRender();
                if (mainWindowViewModel.CurrentViewModel is GetStartedViewModel)
                    AssertSameOrganizationRequirement(provider, window);
                if (scenario.StartsWith("navigation-help-", StringComparison.Ordinal))
                {
                    var help = window.GetVisualDescendants().OfType<InfoButton>().Single(c => c.Name == "PageHelp");
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(help)));
                    var point = help.TranslatePoint(new Point(help.Bounds.Width / 2, help.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(point);
                    PumpRender();
                    Assert.True(ToolTip.GetIsOpen(help));
                    window.MouseMove(new Point(0, 0));
                    PumpRender();
                    help.Focus(NavigationMethod.Tab);
                    PumpRender();
                    Assert.True(ToolTip.GetIsOpen(help));
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    PumpRender();
                    Assert.False(ToolTip.GetIsOpen(help));
                    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                    PumpRender();
                    Assert.True(help.Flyout!.IsOpen);
                    Assert.False(ToolTip.GetIsOpen(help));
                }
            }

            if (scenario.StartsWith("ready-identity", StringComparison.Ordinal) && mainWindowViewModel.CurrentViewModel is GetStartedViewModel identityPage)
            {
                // The page shows who the connections are, and a tenant that is not the expected one.
                var mismatch = scenario.StartsWith("ready-identitymismatch", StringComparison.Ordinal);
                var guard = provider.GetRequiredService<TenantIdentityGuard>();
                guard.Update(new TenantIdentityInfo
                {
                    TeamsTenantId = "6f1c2a9e-0d4b-4c35-9a51-2b7e0c8d1f43",
                    TeamsTenantName = "Contoso AG",
                    TeamsDomains = new[] { "contoso.com" },
                    GraphTenantId = "6f1c2a9e-0d4b-4c35-9a51-2b7e0c8d1f43",
                    GraphAccount = "admin@contoso.com"
                });
                identityPage.ExpectedTenant = mismatch ? "fabrikam.com" : "contoso.com";
                PumpRender();
                return;
            }

            if (scenario.StartsWith("ready-connections", StringComparison.Ordinal))
            {
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(e => e.Name == "ReadinessScroll");
                scroll.ScrollToEnd();
                PumpRender();
                return;
            }
            foreach (var help in window.GetVisualDescendants().OfType<Expander>()
                .Where(e => e.Name == "TelephonyBasics" || e.Name == "SetupChecklist"))
            {
                help.IsExpanded = false;
            }
            if (scenario.StartsWith("novice-basics", StringComparison.Ordinal)
                || scenario.StartsWith("novice-checklist", StringComparison.Ordinal))
            {
                var name = scenario.StartsWith("novice-basics", StringComparison.Ordinal)
                    ? "TelephonyBasics" : "SetupChecklist";
                var expander = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == name);
                expander.IsExpanded = true;
                PumpRender();
                expander.BringIntoView();
                PumpRender();
                return;
            }

            if (scenario.StartsWith("task6-update-banner", StringComparison.Ordinal))
            {
                var stateType = typeof(MainWindowViewModel).GetNestedType("UpdateBannerState", BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("UpdateBannerState not found.");
                var availableState = Enum.Parse(stateType, "Available");
                var setState = typeof(MainWindowViewModel).GetMethod("SetUpdateBannerState", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("SetUpdateBannerState not found.");
                setState.Invoke(mainWindowViewModel, new object?[] { availableState, "3.26.0", null });
                mainWindowViewModel.IsUpdateAvailable = true;
                mainWindowViewModel.IsUpdateBannerVisible = true;
                mainWindowViewModel.CanInstallUpdate = true;
                return;
            }

            if ((string.Equals(scenario, "validation-details", StringComparison.Ordinal)
                    || string.Equals(scenario, "validation-details-de", StringComparison.Ordinal))
                && mainWindowViewModel.CurrentViewModel is WizardViewModel validationWizard)
            {
                validationWizard.ToggleValidationDetailsCommand.Execute(null);
                PumpRender();
                return;
            }

            if (scenario.StartsWith("task6-script-preview", StringComparison.Ordinal))
            {
                var translation = provider.GetRequiredService<ITranslationService>();
                var dialogService = provider.GetRequiredService<IDialogService>() as DialogService
                    ?? throw new InvalidOperationException("DialogService not registered.");
                var title = translation.Get(
                    UiTextKey.DialogPreviewTitle,
                    new Dictionary<string, object?>
                    {
                        ["context"] = translation.Get(UiTextKey.CallQueuesCreateAction)
                    });
                var createState = typeof(DialogService).GetMethod("CreateScriptPreviewDialogForTesting", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("CreateScriptPreviewDialogForTesting not found.");
                var state = createState.Invoke(dialogService, new object?[] { title, "Get-CsCallQueue -Identity cq-Contoso" })
                    ?? throw new InvalidOperationException("Script preview state was not created.");
                var dialog = BuildPreviewScreenshotDialog(state);
                AttachDialogOverlay(window, dialog);
                PumpRender();
                return;
            }

            if (scenario.StartsWith("task6-confirmation", StringComparison.Ordinal))
            {
                var translation = provider.GetRequiredService<ITranslationService>();
                var dialogService = provider.GetRequiredService<IDialogService>() as DialogService
                    ?? throw new InvalidOperationException("DialogService not registered.");
                var title = translation.Get(
                    UiTextKey.DialogConfirmTitle,
                    new Dictionary<string, object?>
                    {
                        ["context"] = translation.Get(UiTextKey.CallQueuesDeleteAction)
                    });
                var message = translation.Get(
                    UiTextKey.CallQueuesDeleteCallQueueConfirm,
                    new Dictionary<string, object?> { ["name"] = "cq-Contoso" });
                var createState = typeof(DialogService).GetMethod("CreateConfirmationWithPreviewDialogForTesting", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("CreateConfirmationWithPreviewDialogForTesting not found.");
                var state = createState.Invoke(dialogService, new object?[] { title, message, "Remove-CsCallQueue -Identity cq-Contoso" })
                    ?? throw new InvalidOperationException("Confirmation dialog state was not created.");
                var dialog = BuildConfirmationScreenshotDialog(state);
                AttachDialogOverlay(window, dialog);
                PumpRender();
                return;
            }

            if (scenario.StartsWith("task6-error", StringComparison.Ordinal))
            {
                var translation = provider.GetRequiredService<ITranslationService>();
                var dialog = BuildMessageScreenshotDialog(
                    translation.Get(UiTextKey.ErrorPowerShellTitle),
                    translation.Get(
                        UiTextKey.ErrorPowerShellMessage,
                        new Dictionary<string, object?> { ["error"] = "Request failed with 404" }),
                    translation.Get(UiTextKey.DialogOk));
                AttachDialogOverlay(window, dialog);
                PumpRender();
                return;
            }

            if ((string.Equals(scenario, "failed", StringComparison.Ordinal)
                    || string.Equals(scenario, "failed-de", StringComparison.Ordinal))
                && mainWindowViewModel.CurrentViewModel is WizardViewModel wizard)
            {
                var translation = provider.GetRequiredService<ITranslationService>();
                const string nativeErrorOutput = "ERROR: Microsoft 365 group could not be created.";
                wizard.Steps[1].IsFailed = true;
                wizard.Steps[1].Result = nativeErrorOutput;
                wizard.CurrentStep = 1;
                wizard.StepFailed = true;
                wizard.StepResult = nativeErrorOutput;
                wizard.StatusMessage = translation.Get(
                    UiTextKey.WizardStepFailedStatus,
                    new Dictionary<string, object?> { ["step"] = 2 });
                return;
            }

            if (mainWindowViewModel.CurrentViewModel is WizardViewModel progressWizard)
            {
                if (scenario.StartsWith("failed-step-error", StringComparison.Ordinal))
                {
                    const string output = "SUCCESS: Resource account racq-contoso-reception@contoso.onmicrosoft.com created successfully\nERROR: Length cannot be less than zero. (Parameter 'length')";
                    progressWizard.Steps[0].IsCompleted = true;
                    progressWizard.Steps[1].IsCompleted = true;
                    progressWizard.Steps[1].Result = "SUCCESS: ttgrp-contoso-reception created successfully";
                    progressWizard.Steps[2].IsFailed = true;
                    progressWizard.Steps[2].Result = output;
                    progressWizard.Steps[2].ErrorMessage = "Length cannot be less than zero. (Parameter 'length')";
                    progressWizard.CurrentStep = 2;
                    return;
                }

                if (scenario.StartsWith("ready-progress", StringComparison.Ordinal))
                {
                    for (var index = 0; index <= 3; index++)
                    {
                        progressWizard.Steps[index].IsCompleted = true;
                        progressWizard.Steps[index].ConfigurationFingerprint = progressWizard.ComputeStepFingerprint(index);
                    }
                    progressWizard.Steps[1].Result = "SUCCESS: ttgrp-contoso-reception created successfully with ID: 6f1c2a9e-0d4b-4c35-9a51-2b7e0c8d1f43";
                    progressWizard.Steps[2].Result = "SUCCESS: Resource account racq-contoso-reception@contoso.onmicrosoft.com created successfully";
                    // Steps 2 and 3 ran before the operator corrected the Configuration.
                    progressWizard.Steps[2].ConfigurationFingerprint = "earlier";
                    progressWizard.Steps[3].ConfigurationFingerprint = "earlier";
                    progressWizard.CurrentStep = 3;
                    progressWizard.GoToStepCommand.Execute(progressWizard.Steps[2]);
                    return;
                }

                if (scenario.StartsWith("ready-callqueue-step", StringComparison.Ordinal) || scenario.StartsWith("ready-last-step", StringComparison.Ordinal))
                {
                    // The step that explains the agent delay, and the step that sets the caller ID.
                    var shown = scenario.StartsWith("ready-callqueue-step", StringComparison.Ordinal) ? 4 : 8;
                    for (var index = 0; index < shown; index++)
                    {
                        progressWizard.Steps[index].IsCompleted = true;
                        progressWizard.Steps[index].ConfigurationFingerprint = progressWizard.ComputeStepFingerprint(index);
                    }
                    progressWizard.CurrentStep = shown;
                    return;
                }

                if (scenario.StartsWith("ready-plan", StringComparison.Ordinal))
                {
                    progressWizard.Steps[0].IsCompleted = true;
                    progressWizard.CurrentStep = 1;
                    progressWizard.GeneratePlanCommand.Execute(null);
                    if (scenario.StartsWith("ready-plan-live", StringComparison.Ordinal) && progressWizard.Plan is not null)
                    {
                        // What a tenant check shows: one object exists, a license is free, the number is taken.
                        var sources = new PhoneDesk.Models.IPhoneManagerVariables[] { progressWizard.Variables };
                        progressWizard.Plan = PhoneDesk.Operations.TenantPreflight.Apply(progressWizard.Plan, sources, new PhoneDesk.Operations.PreflightTenantState
                        {
                            LicensesRead = true,
                            Licenses = new[] { new PhoneDesk.Operations.PreflightLicense { SkuId = progressWizard.Variables.SkuId, Name = "PHONESYSTEM_VIRTUALUSER", Available = 3 } },
                            Rows = new[]
                            {
                                new PhoneDesk.Operations.PreflightTenantRow
                                {
                                    Row = 1,
                                    Groups = 1,
                                    CallQueues = 0,
                                    AutoAttendants = 0,
                                    CallQueueAccount = new PhoneDesk.Operations.PreflightAccount(),
                                    AutoAttendantAccount = new PhoneDesk.Operations.PreflightAccount(),
                                    Number = new PhoneDesk.Operations.PreflightNumber { InInventory = true, Type = progressWizard.Variables.PhoneNumberType, AssignedToId = "1", AssignedTo = "anna.muster@contoso.com" }
                                }
                            }
                        });
                    }

                    return;
                }
            }

            switch (mainWindowViewModel.CurrentViewModel)
            {
                case M365GroupsViewModel m365 when scenario.StartsWith("task5-m365-empty", StringComparison.Ordinal):
                    m365.Groups.Clear();
                    m365.SearchText = string.Empty;
                    m365.GroupStatus = string.Empty;
                    m365.GroupId = string.Empty;
                    m365.IsGroupChecked = false;
                    m365.ShowConfirmation = false;
                    m365.ShowCreateGroupDialog = false;
                    break;

                case CallQueuesViewModel callQueues when scenario.StartsWith("task5-callqueues-populated", StringComparison.Ordinal):
                    {
                    var translation = provider.GetRequiredService<ITranslationService>();
                    callQueues.ResourceAccounts.Clear();
                    callQueues.CallQueues.Clear();
                    callQueues.SearchResourceAccountsText = string.Empty;
                    callQueues.SearchCallQueuesText = string.Empty;
                    callQueues.ShowCreateResourceAccountDialog = false;
                    callQueues.ShowCreateCallQueueDialog = false;
                    callQueues.ShowAssociateDialog = false;
                    callQueues.ShowUpdateUsageLocationDialog = false;
                    callQueues.StatusMessage = translation.Get(
                        UiTextKey.CallQueuesLoadResourceAccountsSuccess,
                        new Dictionary<string, object?> { ["count"] = 2, ["prefix"] = "racq-" });
                    callQueues.ResourceAccounts.Add(new ResourceAccount("Reception Zurich", "racq-zurich@contoso.com", "Identity-RACQ-001", "CH"));
                    callQueues.ResourceAccounts.Add(new ResourceAccount("Service Bern", "racq-bern@contoso.com", "Identity-RACQ-002", "CH"));
                    callQueues.CallQueues.Add(new CallQueue("cq-Contoso Reception", "Identity-CQ-001", "Longest Idle", 30));
                    break;
                    }

                case AutoAttendantsViewModel autoAttendants when scenario.StartsWith("task5-autoattendants-filter", StringComparison.Ordinal):
                    {
                    var translation = provider.GetRequiredService<ITranslationService>();
                    autoAttendants.ResourceAccounts.Clear();
                    autoAttendants.AutoAttendants.Clear();
                    autoAttendants.SearchResourceAccountsText = string.Empty;
                    autoAttendants.SearchAutoAttendantsText = string.Empty;
                    autoAttendants.ShowCreateResourceAccountDialog = false;
                    autoAttendants.ShowCreateAutoAttendantDialog = false;
                    autoAttendants.ShowAssociateDialog = false;
                    autoAttendants.ShowValidateCallQueueDialog = false;
                    autoAttendants.ShowCreateCallTargetDialog = false;
                    autoAttendants.ShowCreateDefaultCallFlowDialog = false;
                    autoAttendants.ShowCreateAfterHoursCallFlowDialog = false;
                    autoAttendants.ShowCreateAfterHoursScheduleDialog = false;
                    autoAttendants.ShowCreateCallHandlingAssociationDialog = false;
                    autoAttendants.StatusMessage = translation.Get(
                        UiTextKey.AutoAttendantsLoadResourceAccountsSuccess,
                        new Dictionary<string, object?> { ["count"] = 2, ["prefix"] = "raaa-" });
                    autoAttendants.ResourceAccounts.Add(new ResourceAccount("Reception Zurich", "raaa-zurich@contoso.com", "Identity-RAAA-001", "CH"));
                    autoAttendants.ResourceAccounts.Add(new ResourceAccount("Support Basel", "raaa-basel@contoso.com", "Identity-RAAA-002", "CH"));
                    autoAttendants.SearchResourceAccountsText = "Basel";
                    break;
                    }

                case HolidaysViewModel holidays when scenario.StartsWith("task5-holidays-dialog", StringComparison.Ordinal):
                    {
                    var translation = provider.GetRequiredService<ITranslationService>();
                    holidays.ShowCreateHolidayDialog = false;
                    holidays.ShowCheckAutoAttendantDialog = false;
                    holidays.ShowAttachHolidayDialog = true;
                    holidays.HolidayName = "hd-contoso-nationalday";
                    holidays.AutoAttendantName = "aa-Contoso Reception";
                    holidays.StatusMessage = translation.Get(
                        UiTextKey.HolidaysCreateHolidaySeriesSuccessLog,
                        new Dictionary<string, object?> { ["name"] = "hd-contoso-nationalday" });
                    break;
                    }

                case BulkOperationsViewModel bulkOperations when scenario.StartsWith("task5-bulk-error", StringComparison.Ordinal):
                    {
                    var translation = provider.GetRequiredService<ITranslationService>();
                    bulkOperations.IsExecuting = false;
                    bulkOperations.SkipInvalidRows = true;
                    bulkOperations.TotalCount = 0;
                    bulkOperations.ProcessedCount = 0;
                    bulkOperations.Plan = null;
                    bulkOperations.ParsedEntries.Clear();
                    bulkOperations.CsvContent = "Customer,CustomerGroupName\ncontoso";
                    bulkOperations.ScriptPreview = string.Empty;
                    var errorDetails = translation.Get(UiTextKey.BulkOperationsMissingRequiredColumnsError);
                    bulkOperations.ExecutionLog = translation.Get(
                        UiTextKey.BulkOperationsParseErrorLog,
                        new Dictionary<string, object?> { ["error"] = errorDetails });
                    bulkOperations.StatusMessage = translation.Get(
                        UiTextKey.BulkOperationsParseErrorStatus,
                        new Dictionary<string, object?> { ["error"] = errorDetails });
                    break;
                    }

                case VariablesViewModel holidaysVariables when scenario.StartsWith("holiday-audit-", StringComparison.Ordinal):
                    holidaysVariables.OpenPredefinedHolidaysWizardCommand.Execute(null);
                    holidaysVariables.SelectedYear = 2026;
                    if (scenario.Contains("-fr-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedCanton = "Fribourg";
                        if (scenario.Contains("-catholic-", StringComparison.Ordinal)) holidaysVariables.SelectedHolidayRegionIndex = 0;
                        if (scenario.Contains("-reformed-", StringComparison.Ordinal)) holidaysVariables.SelectedHolidayRegionIndex = 1;
                    }
                    else if (scenario.Contains("-so-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedCanton = "Solothurn";
                        holidaysVariables.SelectedHolidayRegionIndex = scenario.Contains("-bucheggberg-", StringComparison.Ordinal) ? 0 : 1;
                    }
                    else if (scenario.Contains("-ti-", StringComparison.Ordinal) || scenario.Contains("-manager-", StringComparison.Ordinal))
                        holidaysVariables.SelectedCanton = "Tessin";
                    else if (scenario.Contains("-ge-", StringComparison.Ordinal))
                        holidaysVariables.SelectedCanton = "Genf";
                    else if (scenario.Contains("-de-by-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedCountry = "Germany";
                        holidaysVariables.SelectedProviderRegion = holidaysVariables.ProviderRegions.Single(region => region.Code == "BY-KATH");
                    }
                    else if (scenario.Contains("-gb-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedCountry = "United Kingdom";
                    }
                    else if (scenario.Contains("-ics-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedYear = 2027;
                        holidaysVariables.LoadHolidayCalendar(string.Join('\n', new[]
                        {
                            "BEGIN:VCALENDAR",
                            "BEGIN:VEVENT", "DTSTART;VALUE=DATE:20270101", "SUMMARY:Nieuwjaarsdag", "END:VEVENT",
                            "BEGIN:VEVENT", "DTSTART;VALUE=DATE:20270427", "SUMMARY:Koningsdag", "RRULE:FREQ=YEARLY", "END:VEVENT",
                            "BEGIN:VEVENT", "DTSTART:20271224T120000", "DTEND:20271224T180000", "SUMMARY:Kerstavond middag", "END:VEVENT",
                            "BEGIN:VEVENT", "DTSTART;VALUE=DATE:20271227", "DTEND;VALUE=DATE:20280103", "SUMMARY:Bedrijfssluiting", "END:VEVENT",
                            "BEGIN:VEVENT", "SUMMARY:Zonder datum", "END:VEVENT",
                            "END:VCALENDAR"
                        }));
                    }
                    else if (scenario.Contains("-ag-open-", StringComparison.Ordinal))
                    {
                        holidaysVariables.SelectedCanton = "Aargau";
                        holidaysVariables.SelectedBezirk = holidaysVariables.Bezirke[3];
                        PumpRender();
                        window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AargauHolidayRegion").IsDropDownOpen = true;
                    }
                    if (scenario.Contains("-manager-", StringComparison.Ordinal))
                    {
                        holidaysVariables.ApplyPredefinedHolidaysCommand.Execute(null);
                        holidaysVariables.OpenHolidaySeriesManagerCommand.Execute(null);
                    }
                    break;

                case VariablesViewModel callerId when scenario.StartsWith("ready-callerid", StringComparison.Ordinal):
                    callerId.Variables.CqUseAutoAttendantNumberAsCallerId = true;
                    callerId.OpenCallQueueConfigurationDialogCommand.Execute(null);
                    PumpRender();
                    window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "CallQueueCallerIdToggle")
                        .GetVisualAncestors().OfType<ScrollViewer>().First().ScrollToEnd();
                    break;

                case VariablesViewModel routing when scenario.StartsWith("ready-routing", StringComparison.Ordinal):
                    if (scenario.StartsWith("ready-routing-audio", StringComparison.Ordinal))
                    {
                        // The audio file ID can be typed, to use a file that was uploaded before.
                        routing.Variables.AaDefaultGreetingType = "AudioFile";
                        routing.Variables.AaDefaultGreetingAudioFileId = "a0a0a0a0-0000-0000-0000-00000000000a";
                    }
                    routing.OpenAutoAttendantConfigurationDialogCommand.Execute(null);
                    break;

                case VariablesViewModel variables when scenario.StartsWith("issue71-config-import", StringComparison.Ordinal):
                    variables.PendingConfigurationFileName = "contoso-production.json";
                    variables.PendingConfigurationChanges.Clear();
                    variables.PendingConfigurationChanges.Add(new ConfigurationChange(
                        "configuration.general.customer", "contoso", "fabrikam"));
                    variables.PendingConfigurationChanges.Add(new ConfigurationChange(
                        "configuration.general.usageLocation", "CH", "DE"));
                    variables.PendingConfigurationChanges.Add(new ConfigurationChange(
                        "configuration.callQueueTemplate.overflowThreshold", "10", "25"));
                    variables.PendingConfigurationChanges.Add(new ConfigurationChange(
                        "configuration.holidayTemplate.series", "2 entries", "4 entries"));
                    variables.ShowConfigurationImportPreview = true;
                    break;

                case DocumentationViewModel documentation when scenario.StartsWith("issue71-topology-drift", StringComparison.Ordinal):
                    documentation.SetTopologyDriftForDisplay(
                        "contoso-baseline-20260801.json",
                        new[]
                        {
                            new TopologyDriftEntry(
                                TopologyDriftKind.Changed, "autoAttendant", "aa-1", "Main Reception",
                                "languageId", "de-DE", "en-US"),
                            new TopologyDriftEntry(
                                TopologyDriftKind.Changed, "callQueue", "cq-1", "Support Queue",
                                "agentAlertTime", "30", "45"),
                            new TopologyDriftEntry(
                                TopologyDriftKind.Changed, "phoneNumber", "+41440000000", "+41 44 000 00 00",
                                "city", "Zurich", "Bern"),
                            new TopologyDriftEntry(
                                TopologyDriftKind.Changed, "autoAttendantMenuOption", "aa-1/menu-1",
                                "Reception / DefaultCallFlow / 1", "targetId", "cq-old", "cq-new"),
                            new TopologyDriftEntry(
                                TopologyDriftKind.Added, "resourceAccount", "ra-2", "After-hours RA",
                                null, null, null),
                            new TopologyDriftEntry(
                                TopologyDriftKind.Removed, "group", "group-old", "Legacy Support",
                                null, null, null),
                        });
                    break;

                case HolidayManagerViewModel holidayManager when scenario.StartsWith("holidaymanager-", StringComparison.Ordinal) && !scenario.Contains("-empty-", StringComparison.Ordinal):
                    holidayManager.ShowStateForDisplay(HolidayManagerSampleState());
                    holidayManager.SelectedSchedule = holidayManager.Schedules.First(row => row.Name == "hd-contoso-2027");
                    holidayManager.SelectedHoliday = holidayManager.Holidays.First(row => !row.IsExpired);
                    if (scenario.Contains("-dates-", StringComparison.Ordinal))
                    {
                        holidayManager.OpenEditDatesCommand.Execute(null);
                        holidayManager.OperationText = "2027-12-24 12:00 to 2027-12-25 00:00\n2027-12-25 to 2027-12-26\n2027-12-31 to 2028-01-02";
                        holidayManager.CheckForDisplay();
                    }
                    else if (scenario.Contains("-greeting-", StringComparison.Ordinal))
                    {
                        holidayManager.OpenReplaceGreetingCommand.Execute(null);
                        holidayManager.OperationText = "Unsere Büros sind über die Festtage geschlossen. Ab dem 3. Januar sind wir wieder für Sie da.";
                        holidayManager.CheckForDisplay();
                    }
                    else if (scenario.Contains("-attach-", StringComparison.Ordinal))
                    {
                        holidayManager.OpenAttachCommand.Execute(null);
                        foreach (var choice in holidayManager.AttendantChoices)
                        {
                            choice.IsSelected = true;
                        }
                        holidayManager.CheckForDisplay();
                    }
                    else if (scenario.Contains("-compare-", StringComparison.Ordinal))
                    {
                        holidayManager.OpenCompareListCommand.Execute(null);
                        holidayManager.OperationSecondText = "2027";
                        holidayManager.OperationText = "25.12.2027 Weihnachten\n24.12.2027 12:00-24:00 Heiligabend Nachmittag\nG 01.08. Bundesfeier\nOstern (Datum folgt)";
                        holidayManager.CheckForDisplay();
                    }
                    else if (scenario.Contains("-retire-", StringComparison.Ordinal))
                    {
                        holidayManager.SelectedSchedule = holidayManager.Schedules.First(row => row.IsExpired);
                        holidayManager.OpenRetireCommand.Execute(null);
                        holidayManager.CheckForDisplay();
                    }
                    break;

                case PhoneNumbersViewModel phoneNumbers when scenario.StartsWith("phonenumbers-", StringComparison.Ordinal) && !scenario.Contains("-empty-", StringComparison.Ordinal):
                    {
                    static object NumberOwner(string id, string upn, string name, string account, string number) => new
                    {
                        id,
                        userPrincipalName = upn,
                        displayName = name,
                        accountType = account,
                        phoneNumber = number,
                        featureTypes = new[] { account == "User" ? "PhoneSystem" : "VoiceApp" }
                    };
                    var anna = NumberOwner("11111111-aaaa-0000-0000-000000000001", "anna.muster@contoso.com", "Anna Muster", "User", "+41441110001");
                    var ben = NumberOwner("22222222-bbbb-0000-0000-000000000002", "ben.beispiel@contoso.com", "Ben Beispiel", "User", "");
                    var reception = NumberOwner("33333333-cccc-0000-0000-000000000003", "raaa-contoso-main-reception@contoso.onmicrosoft.com", "raaa-contoso-main-reception", "ResourceAccount", "+41441110000");
                    phoneNumbers.ShowRowsForDisplay(PhoneNumbers.ListMarker + System.Text.Json.JsonSerializer.Serialize(new object[]
                    {
                        new { number = "+41441110000", type = "OperatorConnect", status = "VoiceApplicationAssigned", assignedToId = "33333333-cccc-0000-0000-000000000003", city = "Zürich", capabilities = new[] { "VoiceApplicationAssignment" }, owner = reception },
                        new { number = "+41441110001", type = "CallingPlan", status = "UserAssigned", assignedToId = "11111111-aaaa-0000-0000-000000000001", city = "Zürich", capabilities = new[] { "UserAssignment" }, owner = anna },
                        new { number = "+41441110002", type = "CallingPlan", status = "Unassigned", assignedToId = "", city = "Zürich", capabilities = new[] { "UserAssignment" }, owner = (object?)null },
                        new { number = "+41441110003", type = "DirectRouting", status = "Unassigned", assignedToId = "", city = "", capabilities = new[] { "UserAssignment", "VoiceApplicationAssignment" }, owner = (object?)null }
                    }));
                    phoneNumbers.SelectedNumber = phoneNumbers.Numbers[1];
                    var checkState = PhoneNumbers.CheckMarker + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        number = new { number = "+41441110001", type = "CallingPlan", status = "UserAssigned", assignedToId = "11111111-aaaa-0000-0000-000000000001", city = "Zürich", capabilities = new[] { "UserAssignment" }, owner = anna },
                        target = scenario.Contains("-move-", StringComparison.Ordinal) ? ben : null,
                        targetError = (string?)null
                    });
                    if (scenario.Contains("-move-", StringComparison.Ordinal))
                    {
                        phoneNumbers.OpenAssignCommand.Execute(null);
                        phoneNumbers.DialogTarget = "ben.beispiel@contoso.com";
                        phoneNumbers.AllowReassign = true;
                        phoneNumbers.CheckForDisplay(checkState);
                    }
                    else if (scenario.Contains("-unassign-", StringComparison.Ordinal))
                    {
                        phoneNumbers.OpenUnassignCommand.Execute(null);
                        phoneNumbers.CheckForDisplay(checkState);
                    }
                    break;
                    }

                case HealthCheckViewModel healthCheck when scenario.StartsWith("issue78-health-findings", StringComparison.Ordinal):
                    healthCheck.SetResultsForDisplay(
                        TenantHealthEvaluationServiceTests.CreateContext(),
                        new TenantHealthPreferences(
                            new HashSet<string>(StringComparer.Ordinal)
                            {
                                TenantHealthRuleCatalog.UnassignedServiceNumber,
                            },
                            new HashSet<string>(StringComparer.Ordinal)
                            {
                                $"{TenantHealthRuleCatalog.OrphanedResourceAccount}:unassociated:ra-unused",
                            }),
                        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
                    healthCheck.ShowSuppressed = true;
                    break;

                case HealthCheckViewModel suppressedHealthCheck when scenario.StartsWith("issue78-health-suppressed", StringComparison.Ordinal):
                    var healthContext = TenantHealthEvaluationServiceTests.CreateContext();
                    var allRuleIds = TenantHealthRuleCatalog.Rules
                        .Select(rule => rule.Id)
                        .ToHashSet(StringComparer.Ordinal);
                    var allFindingIds = new TenantHealthEvaluationService()
                        .Evaluate(healthContext, allRuleIds)
                        .Select(finding => finding.FindingId)
                        .ToHashSet(StringComparer.Ordinal);
                    suppressedHealthCheck.SetResultsForDisplay(
                        healthContext,
                        new TenantHealthPreferences(
                            new HashSet<string>(StringComparer.Ordinal),
                            allFindingIds),
                        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
                    suppressedHealthCheck.ShowSuppressed = true;
                    var healthFindingsScroll = window.GetVisualDescendants()
                        .OfType<ScrollViewer>()
                        .Single(scroll => scroll.Name == "HealthFindingsScroll");
                    healthFindingsScroll.Offset = new Vector(0, 150);
                    PumpRender();
                    break;
            }
        }

        private static void AssertSameOrganizationRequirement(ServiceProvider provider, MainWindow window)
        {
            var requirement = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Name == "SameOrganizationRequirement");
            Assert.True(requirement.IsEffectivelyVisible);
            Assert.Equal(ReadinessGuidanceTests.ExpectedSameOrganizationRequirement(
                provider.GetRequiredService<ITranslationService>().CurrentLanguage), requirement.Text);
            Assert.DoesNotContain(requirement.GetVisualAncestors(), v => v is Expander or InfoButton);
            Assert.True(requirement.Bounds.Width > 0 && requirement.Bounds.Height > 0);

            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(v => v.Name == "ReadinessScroll");
            var position = requirement.TranslatePoint(default, window)!.Value;
            var scrollPosition = scroll.TranslatePoint(default, window)!.Value;
            Assert.InRange(position.X, scrollPosition.X, window.Bounds.Width - requirement.Bounds.Width);
            Assert.InRange(position.Y, scrollPosition.Y, Math.Min(window.Bounds.Height,
                scrollPosition.Y + scroll.Bounds.Height) - requirement.Bounds.Height);

            var firstAction = window.GetVisualDescendants().OfType<Button>().Single(button =>
                ReferenceEquals(button.Command, ((GetStartedViewModel)requirement.DataContext!).CheckModulesCommand));
            Assert.True(position.Y + requirement.Bounds.Height <= firstAction.TranslatePoint(default, window)!.Value.Y,
                "The same-organization instruction must be visible above the setup and sign-in actions.");
        }

        private static ShotContext CreateShotContext(ServiceProvider provider)
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = LogicalWidth,
                Height = LogicalHeight,
            };
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }
            window.Show();
            ForceRenderScaling(window, Scale);
            PumpRender();

            var navList = window.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(l => l.Name == "NavigationListBox")
                ?? throw new InvalidOperationException("NavigationListBox not found in MainWindow.");

            return new ShotContext(window, vm, navList);
        }

        private static Control BuildPreviewScreenshotDialog(object state)
        {
            var title = GetStateProperty(state, "Title");
            var primaryButtonText = GetStateProperty(state, "PrimaryButtonText");
            var secondaryButtonText = GetStateProperty(state, "SecondaryButtonText");
            var chromeText = GetStateProperty(state, "ChromeText");
            var watermark = GetStateProperty(state, "Watermark");
            var scriptBody = GetStateProperty(state, "ScriptBody");

            return BuildDialogCard(
                title,
                new StackPanel
                {
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = chromeText,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 22,
                            Foreground = new SolidColorBrush(Color.Parse("#EDEBFF"))
                        },
                        BuildScriptBox(scriptBody, watermark)
                    }
                },
                secondaryButtonText,
                primaryButtonText);
        }

        private static Control BuildConfirmationScreenshotDialog(object state)
        {
            var title = GetStateProperty(state, "Title");
            var message = GetStateProperty(state, "Message");
            var primaryButtonText = GetStateProperty(state, "PrimaryButtonText");
            var secondaryButtonText = GetStateProperty(state, "SecondaryButtonText");
            var chromeText = GetStateProperty(state, "ChromeText");
            var watermark = GetStateProperty(state, "Watermark");
            var scriptBody = GetStateProperty(state, "ScriptBody");

            return BuildDialogCard(
                title,
                new StackPanel
                {
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 22,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#F2C89A"))
                        },
                        new TextBlock
                        {
                            Text = chromeText,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 22,
                            Foreground = new SolidColorBrush(Color.Parse("#EDEBFF"))
                        },
                        BuildScriptBox(scriptBody, watermark)
                    }
                },
                secondaryButtonText,
                primaryButtonText);
        }

        private static Control BuildMessageScreenshotDialog(string title, string message, string primaryButtonText)
        {
            return BuildDialogCard(
                title,
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 22,
                    Foreground = new SolidColorBrush(Color.Parse("#EDEBFF"))
                },
                null,
                primaryButtonText);
        }

        private static Border BuildDialogCard(string title, Control body, string? secondaryButtonText, string primaryButtonText)
        {
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 16,
            };

            if (!string.IsNullOrWhiteSpace(secondaryButtonText))
            {
                buttonRow.Children.Add(BuildDialogButton(secondaryButtonText, isPrimary: false));
            }

            buttonRow.Children.Add(BuildDialogButton(primaryButtonText, isPrimary: true));

            return new Border
            {
                Width = 1120,
                MaxWidth = 1120,
                Padding = new Thickness(48),
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(Color.Parse("#2B2942")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3A3758")),
                BorderThickness = new Thickness(2),
                Child = new StackPanel
                {
                    Spacing = 28,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 42,
                            FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White,
                            TextWrapping = TextWrapping.Wrap
                        },
                        body,
                        buttonRow
                    }
                }
            };
        }

        private static Border BuildDialogButton(string text, bool isPrimary)
        {
            return new Border
            {
                MinWidth = 220,
                Padding = new Thickness(28, 16),
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.Parse(isPrimary ? "#6D6FD0" : "#3A3758")),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private static Border BuildScriptBox(string scriptBody, string watermark)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#35334B")),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(24),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = watermark,
                            FontSize = 18,
                            Foreground = new SolidColorBrush(Color.Parse("#B7B4D9"))
                        },
                        new TextBox
                        {
                            Text = scriptBody,
                            IsReadOnly = true,
                            AcceptsReturn = true,
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace"),
                            FontSize = 20,
                            Background = Brushes.Transparent,
                            Foreground = Brushes.White
                        }
                    }
                }
            };
        }

        private static string GetStateProperty(object state, string name)
        {
            var property = state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"State property '{name}' not found.");
            return property.GetValue(state)?.ToString()
                ?? throw new InvalidOperationException($"State property '{name}' was null.");
        }

        private static void AttachDialogOverlay(Window window, Control dialog)
        {
            var originalContent = window.Content as Control
                ?? throw new InvalidOperationException("Window content was not a control.");

            window.Content = null;

            dialog.HorizontalAlignment = HorizontalAlignment.Center;
            dialog.VerticalAlignment = VerticalAlignment.Center;
            dialog.MinWidth = 720;
            dialog.MaxWidth = 1440;

            var host = new Grid();
            host.Children.Add(originalContent);
            host.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(160, 6, 7, 19))
            });
            host.Children.Add(dialog);

            window.Content = host;
        }

        private sealed record ShotContext(MainWindow Window, MainWindowViewModel ViewModel, ListBox NavigationList) : IDisposable
        {
            public void Dispose()
            {
                ViewModel.Dispose();
                Window.Close();
            }
        }

        private static void PumpRender()
        {
            // Drain layout/render jobs and advance the headless render clock well past the longest
            // view animation (~0.6s entrance + 0.15s CrossFade). Non-repeating animations settle on
            // their final KeyFrame, so we capture the fully-rendered, steady state.
            for (var i = 0; i < 150; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }

        private static Bitmap CaptureContent(MainWindow window)
        {
            // The compositor render (what the render thread actually drew) is the faithful path:
            // FluentIcons filled glyphs render correctly here, unlike RenderTargetBitmap.Render.
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("CaptureRenderedFrame returned null.");

            if (frame.PixelSize.Width != PixelWidth || frame.PixelSize.Height != ContentPixelHeight)
            {
                throw new InvalidOperationException(
                    $"Captured frame is {frame.PixelSize.Width}x{frame.PixelSize.Height}, " +
                    $"expected {PixelWidth}x{ContentPixelHeight} (render scaling not applied).");
            }

            return frame;
        }

        /// <summary>
        /// Forces the headless window to render at the given device scale so the compositor output
        /// is 2x (retina) crisp. The headless window impl exposes RenderScaling only via a getter;
        /// we set its backing field and raise ScalingChanged so the TopLevel relayouts/resizes.
        /// </summary>
        /// <summary>Two main numbers with holidays of three years, for the Holiday Manager renders.</summary>
        private static string HolidayManagerSampleState()
        {
            static object Flow(string id, string greeting, string action, string targetType, string target) => new
            {
                id,
                name = "flow",
                greetings = greeting.Length == 0 ? Array.Empty<object>() : new object[] { new { type = "TextToSpeech", text = greeting, audioFile = (object?)null } },
                menuName = "menu",
                menuPrompts = Array.Empty<object>(),
                options = new[]
                {
                    new
                    {
                        action,
                        key = "Automatic",
                        voiceResponses = Array.Empty<string>(),
                        target = action == "DisconnectCall" ? null : new { id = target, type = targetType, enableTranscription = (bool?)null, suppressSystemPrompt = (bool?)null, callPriority = 0 },
                        prompt = (object?)null
                    }
                },
                forceListenMenu = false,
                dialByName = false
            };
            static object Link(string schedule, string flow) => new { type = "Holiday", scheduleId = schedule, callFlowId = flow, enabled = true, priority = 0 };
            static object Schedule(string id, string name, string[][] windows, params string[] usedBy) => new
            {
                id,
                name,
                type = "Fixed",
                windows = windows.Select(window => new { start = window[0], end = window[1] }),
                usedBy
            };
            const string main = "aaaaaaaa-0000-0000-0000-000000000001";
            const string branch = "aaaaaaaa-0000-0000-0000-000000000002";
            const string y2024 = "33333333-0000-0000-0000-000000000003";
            const string y2026 = "11111111-0000-0000-0000-000000000001";
            const string y2027 = "22222222-0000-0000-0000-000000000002";
            const string closure = "44444444-0000-0000-0000-000000000004";
            return HolidayCallHandling.JsonMarker + System.Text.Json.JsonSerializer.Serialize(new
            {
                autoAttendants = new object[]
                {
                    new
                    {
                        id = main,
                        name = "aa-contoso-main-reception",
                        authorizedUsers = Array.Empty<string>(),
                        callFlows = new[]
                        {
                            Flow("f1", "Wir sind heute wegen eines Feiertags nicht erreichbar. Bitte rufen Sie am nächsten Werktag wieder an.", "DisconnectCall", "", ""),
                            Flow("f2", "Our office is closed.", "DisconnectCall", "", ""),
                            Flow("f3", "", "TransferCallToTarget", "ExternalPstn", "tel:+41441112233")
                        },
                        associations = new[] { Link(y2026, "f1"), Link(y2024, "f2"), Link(closure, "f3") }
                    },
                    new
                    {
                        id = branch,
                        name = "aa-contoso-branch-zurich",
                        authorizedUsers = Array.Empty<string>(),
                        callFlows = new[] { Flow("f4", "Bitte hinterlassen Sie eine Nachricht.", "TransferCallToTarget", "SharedVoicemail", "6f1c2a9e-0d4b-4c35-9a51-2b7e0c8d1f43") },
                        associations = new[] { Link(y2026, "f4") }
                    }
                },
                schedules = new[]
                {
                    Schedule(y2024, "hd-contoso-2024", new[] { new[] { "2024-12-25T00:00:00", "2024-12-27T00:00:00" } }, main),
                    Schedule(y2026, "hd-contoso-2026", new[] { new[] { "2026-12-25T00:00:00", "2026-12-27T00:00:00" }, new[] { "2027-01-01T00:00:00", "2027-01-03T00:00:00" } }, main, branch),
                    Schedule(closure, "hd-contoso-betriebsferien-2027", new[] { new[] { "2027-07-19T00:00:00", "2027-08-02T00:00:00" } }, main),
                    Schedule(y2027, "hd-contoso-2027", new[] { new[] { "2027-12-25T00:00:00", "2027-12-27T00:00:00" } })
                },
                missingAuthorizedUsers = Array.Empty<object>(),
                uncheckedAuthorizedUsers = Array.Empty<object>(),
                disabledAuthorizedUsers = Array.Empty<object>()
            });
        }

        private static void ForceRenderScaling(MainWindow window, double scaling)
        {
            var implProperty = typeof(TopLevel).GetProperty(
                "PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException("TopLevel.PlatformImpl not found.");
            var impl = implProperty.GetValue(window)
                ?? throw new InvalidOperationException("Window has no PlatformImpl.");

            var implType = impl.GetType();
            var backingField = implType.GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("HeadlessWindowImpl RenderScaling backing field not found.");
            backingField.SetValue(impl, scaling);

            var scalingChanged = implType.GetProperty("ScalingChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(impl) as Action<double>;
            scalingChanged?.Invoke(scaling);

            Dispatcher.UIThread.RunJobs();
        }

        private static void Composite(Bitmap content, bool dark, string path)
        {
            if (Frameless)
            {
                content.Save(path);
                return;
            }

            var final = new RenderTargetBitmap(new PixelSize(PixelWidth, PixelHeight), new Vector(96, 96));
            using (var ctx = final.CreateDrawingContext())
            {
                // Title bar background (sampled from the original screenshots).
                var barColor = dark ? Color.FromRgb(56, 56, 56) : Color.FromRgb(237, 237, 237);
                ctx.FillRectangle(new SolidColorBrush(barColor), new Rect(0, 0, PixelWidth, TitleBarPixelHeight));
                if (dark)
                {
                    // Subtle 1px top highlight present on the macOS dark title bar.
                    ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(128, 128, 128)), new Rect(0, 0, PixelWidth, 1));
                }

                // Traffic lights (centres/colours sampled from originals).
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(236, 106, 94)), null, new Point(27, 28), 10, 10);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(244, 191, 79)), null, new Point(67, 28), 10, 10);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(97, 197, 84)), null, new Point(107, 28), 10, 10);

                // Centred window title.
                var titleColor = dark ? Color.FromRgb(165, 165, 165) : Color.FromRgb(83, 83, 83);
                var title = new FormattedText(
                    "PhoneDesk",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Helvetica Neue", FontStyle.Normal, FontWeight.SemiBold),
                    26,
                    new SolidColorBrush(titleColor));
                ctx.DrawText(title, new Point((PixelWidth - title.Width) / 2, (TitleBarPixelHeight - title.Height) / 2));

                // App content below the title bar.
                ctx.DrawImage(
                    content,
                    new Rect(0, 0, PixelWidth, ContentPixelHeight),
                    new Rect(0, TitleBarPixelHeight, PixelWidth, ContentPixelHeight));
            }

            final.Save(path);
        }

        private readonly record struct FrameStats(int Width, int Height, int DistinctColors, int BrandPurplePixels);

        /// <summary>
        /// Rejects blank/near-uniform frames. A real rendered app has thousands of distinct colours;
        /// a black/blank frame has ~1. Also confirms the sidebar brand gradient region actually
        /// contains T-Pad purple (#7B80EE-ish) tones.
        /// </summary>
        private static bool TryValidate(string path, out FrameStats stats)
        {
            using var bmp = new Bitmap(path);
            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;

            var pixels = new byte[w * h * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4);
            }
            finally
            {
                handle.Free();
            }

            var distinct = new HashSet<int>();
            var purple = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                distinct.Add((r << 16) | (g << 8) | b);
                // T-Pad brand gradient runs #7B80EE -> #45478F: blue-dominant mid purples where the
                // blue channel leads red and green by a clear margin (verified against the sampled
                // logo pixel #6063BE / #6368C4).
                if (b > 120 && b >= r + 30 && b >= g + 30 && r is > 50 and < 180 && g is > 50 and < 180)
                {
                    purple++;
                }
            }

            stats = new FrameStats(w, h, distinct.Count, purple);
            return w == PixelWidth
                && h == PixelHeight
                && distinct.Count > 500
                && purple > 200;
        }

        private static ServiceProvider BuildProvider()
        {
            var services = new ServiceCollection();

            // Reuse the app's real composition root so registrations never drift from Program.cs.
            var rootAssembly = Assembly.Load("phonedesk");
            var programType = rootAssembly.GetType("PhoneDesk.Program", throwOnError: true)!;
            var configure = programType.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("PhoneDesk.Program.ConfigureServices not found.");
            configure.Invoke(null, new object[] { services });

            // Swap the only services that would touch PowerShell / MSAL-Graph / the network.
            ReplaceSingleton<IUserPreferencesStore>(services, new ScreenshotPreferences());
            ReplaceSingleton<IAuditLog>(services, new ScreenshotAuditLog());
            ReplaceSingleton<IUpdateCheckService>(services, new StubUpdateCheckService());
            ReplaceSingleton<IPowerShellContextService>(services, new StubPowerShellContextService());
            ReplaceSingleton<IMsalGraphAuthenticationService>(services, new StubMsalGraphAuthenticationService());

            return services.BuildServiceProvider();
        }

        private sealed class ScreenshotAuditLog : IAuditLog
        {
            public string LogDirectoryPath => "C:/Example/PhoneDesk/audit";
            public void Append(PhoneDesk.Audit.AuditRecord record) { }
            public IReadOnlyList<PhoneDesk.Audit.AuditRecord> Read() => new[]
            {
                new PhoneDesk.Audit.AuditRecord
                {
                    TimestampUtc = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero),
                    Operation = "CheckModules", Operator = "operator@example.com", Target = "Example reception",
                    Outcome = PhoneDesk.Audit.AuditOutcome.Success
                }
            };
        }

        private sealed class ScreenshotPreferences : IUserPreferencesStore
        {
            public AppLanguage? LoadLanguage() => AppLanguage.English;
            public void SaveLanguage(AppLanguage language) { }
            public bool LoadIntroductionDismissed() => true;
            public void SaveIntroductionDismissed() { }
        }

        private static void ReplaceSingleton<T>(IServiceCollection services, object implementation) where T : class
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(T))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddSingleton(typeof(T), implementation);
        }

        private sealed class StubUpdateCheckService : IUpdateCheckService
        {
            public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<UpdateInfo?>(null);
        }

        private sealed class StubPowerShellContextService : IPowerShellContextService
        {
            public Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
                => Task.FromResult(string.Empty);

            public Task<string> ExecuteCommandAsync(string command, Dictionary<string, string>? environmentVariables, CancellationToken cancellationToken = default)
                => Task.FromResult(string.Empty);

            public Task<PowerShellExecutionResult> ExecuteCommandWithDetailsAsync(string command, Dictionary<string, string>? environmentVariables, IProgress<PowerShellProgress>? progress = null, CancellationToken cancellationToken = default)
                => Task.FromResult(new PowerShellExecutionResult());

            public Task<bool> IsConnectedAsync(string service, CancellationToken cancellationToken = default)
                => Task.FromResult(false);

            public Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(string.Empty);

            public void Dispose()
            {
            }
        }

        private sealed class StubMsalGraphAuthenticationService : IMsalGraphAuthenticationService
        {
            public Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)> AuthenticateAsync(IntPtr? parentWindowHandle = null)
                => Task.FromResult<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)>((false, null, null, "headless"));

            public Task SignOutAsync() => Task.CompletedTask;

            public Task<bool> HasCachedAccountAsync() => Task.FromResult(false);
        }
    }
}
