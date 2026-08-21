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
        private const int LogicalWidth = 1280;
        private const int LogicalHeight = 720;
        private const double Scale = 2.0;
        private const int PixelWidth = (int)(LogicalWidth * Scale);          // 2560
        private const int ContentPixelHeight = (int)(LogicalHeight * Scale); // 1440
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
            new("Welcome", true, "shell-settings-en.png", "settings-en"),
            new("Welcome", true, "shell-settings-de.png", "settings-de"),
            new("Welcome", true, "welcome.png"),
            new("GetStarted", true, "get-started.png"),
            new("GetStarted", true, "get-started-ready.png", "ready"),
            new("GetStarted", true, "get-started-ready-de.png", "ready-de"),
            new("Variables", true, "variables.png"),
            new("Variables", true, "variables-de.png", "ready-de"),
            new("Dashboard", true, "dashboard-de.png", "ready-de"),
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
            new("Wizard", true, "setup-wizard-ready.png", "ready"),
            new("Wizard", true, "setup-wizard-ready-de.png", "ready-de"),
            new("Wizard", true, "setup-wizard-failed.png", "failed"),
            new("Wizard", true, "setup-wizard-failed-de.png", "failed-de"),
            new("BulkOperations", true, "bulk-operations.png"),
            new("BulkOperations", true, "task5-bulk-operations-error-en.png", "task5-bulk-error-en"),
            new("BulkOperations", true, "task5-bulk-operations-error-de.png", "task5-bulk-error-de"),
            new("Documentation", true, "documentation.png"),
            new("Documentation", true, "documentation-de.png", "ready-de"),
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

            var kept = new List<string>();
            string? previousPage = null;

            foreach (var shot in Shots)
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

            translation.CurrentLanguage = scenario.Contains("-de", StringComparison.Ordinal)
                ? AppLanguage.German
                : AppLanguage.English;

            if (scenario.StartsWith("ready", StringComparison.Ordinal)
                || scenario.StartsWith("failed", StringComparison.Ordinal))
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
            var isGerman = scenario.EndsWith("-de", StringComparison.Ordinal);

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

            if (scenario.StartsWith("task6-script-preview", StringComparison.Ordinal))
            {
                var translation = provider.GetRequiredService<ITranslationService>();
                var dialogService = provider.GetRequiredService<IDialogService>() as DialogService
                    ?? throw new InvalidOperationException("DialogService not registered.");
                var title = translation.CurrentLanguage == AppLanguage.German
                    ? "Vorschau: Anrufwarteschleife erstellen"
                    : "Preview: Create Call Queue";
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
                var title = translation.CurrentLanguage == AppLanguage.German
                    ? "Bestätigen: Anrufwarteschleife löschen"
                    : "Confirm: Delete Call Queue";
                var message = translation.CurrentLanguage == AppLanguage.German
                    ? "Dies löscht die Anrufwarteschleife 'cq-Contoso' dauerhaft. Diese Aktion kann nicht rückgängig gemacht werden."
                    : "This permanently deletes the call queue 'cq-Contoso'. This action cannot be undone.";
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
                wizard.Steps[1].IsFailed = true;
                wizard.Steps[1].Result = "Microsoft 365 group could not be created. Retry the step or skip it after checking the output.";
                wizard.CurrentStep = 1;
                wizard.StepFailed = true;
                wizard.StepResult = wizard.Steps[1].Result;
                return;
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
                    callQueues.ResourceAccounts.Clear();
                    callQueues.CallQueues.Clear();
                    callQueues.SearchResourceAccountsText = string.Empty;
                    callQueues.SearchCallQueuesText = string.Empty;
                    callQueues.ShowCreateResourceAccountDialog = false;
                    callQueues.ShowCreateCallQueueDialog = false;
                    callQueues.ShowAssociateDialog = false;
                    callQueues.ShowUpdateUsageLocationDialog = false;
                    callQueues.StatusMessage = isGerman
                        ? "2 Ressourcenkonten mit dem Präfix „racq-“ gefunden"
                        : "Found 2 resource accounts starting with 'racq-'";
                    callQueues.ResourceAccounts.Add(new ResourceAccount("Reception Zurich", "racq-zurich@contoso.com", "Identity-RACQ-001", "CH"));
                    callQueues.ResourceAccounts.Add(new ResourceAccount("Service Bern", "racq-bern@contoso.com", "Identity-RACQ-002", "CH"));
                    callQueues.CallQueues.Add(new CallQueue("cq-Contoso Reception", "Identity-CQ-001", "Longest Idle", 30));
                    break;

                case AutoAttendantsViewModel autoAttendants when scenario.StartsWith("task5-autoattendants-filter", StringComparison.Ordinal):
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
                    autoAttendants.StatusMessage = isGerman
                        ? "2 Ressourcenkonten mit dem Präfix „raaa-“ gefunden"
                        : "Found 2 resource accounts starting with 'raaa-'";
                    autoAttendants.ResourceAccounts.Add(new ResourceAccount("Reception Zurich", "raaa-zurich@contoso.com", "Identity-RAAA-001", "CH"));
                    autoAttendants.ResourceAccounts.Add(new ResourceAccount("Support Basel", "raaa-basel@contoso.com", "Identity-RAAA-002", "CH"));
                    autoAttendants.SearchResourceAccountsText = "Basel";
                    break;

                case HolidaysViewModel holidays when scenario.StartsWith("task5-holidays-dialog", StringComparison.Ordinal):
                    holidays.ShowCreateHolidayDialog = false;
                    holidays.ShowCheckAutoAttendantDialog = false;
                    holidays.ShowAttachHolidayDialog = true;
                    holidays.HolidayName = "hd-contoso-nationalday";
                    holidays.AutoAttendantName = "aa-Contoso Reception";
                    holidays.StatusMessage = isGerman
                        ? "Feiertagsserie „hd-contoso-nationalday“ erfolgreich erstellt."
                        : "Holiday series 'hd-contoso-nationalday' created successfully.";
                    break;

                case BulkOperationsViewModel bulkOperations when scenario.StartsWith("task5-bulk-error", StringComparison.Ordinal):
                    bulkOperations.IsExecuting = false;
                    bulkOperations.SkipInvalidRows = true;
                    bulkOperations.TotalCount = 0;
                    bulkOperations.ProcessedCount = 0;
                    bulkOperations.Plan = null;
                    bulkOperations.ParsedEntries.Clear();
                    bulkOperations.CsvContent = "Customer,CustomerGroupName\ncontoso";
                    bulkOperations.ScriptPreview = string.Empty;
                    bulkOperations.ExecutionLog = isGerman
                        ? "FEHLER: Erforderliche CSV-Spalten fehlen."
                        : "ERROR: Missing required CSV columns.";
                    bulkOperations.StatusMessage = isGerman
                        ? "Analysefehler: Erforderliche CSV-Spalten fehlen."
                        : "Parse error: Missing required CSV columns.";
                    break;
            }
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
                Width = 1400,
                MaxWidth = 1400,
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
            ReplaceSingleton<IUpdateCheckService>(services, new StubUpdateCheckService());
            ReplaceSingleton<IPowerShellContextService>(services, new StubPowerShellContextService());
            ReplaceSingleton<IMsalGraphAuthenticationService>(services, new StubMsalGraphAuthenticationService());

            return services.BuildServiceProvider();
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
