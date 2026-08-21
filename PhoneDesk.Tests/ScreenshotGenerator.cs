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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk;
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
            new("Welcome", true, "welcome.png"),
            new("GetStarted", true, "get-started.png"),
            new("GetStarted", true, "get-started-ready.png", "ready"),
            new("Variables", true, "variables.png"),
            new("M365Groups", true, "m365-groups.png"),
            new("CallQueues", true, "call-queues.png"),
            new("AutoAttendants", true, "auto-attendants.png"),
            new("Holidays", true, "holidays.png"),
            new("Wizard", true, "setup-wizard.png"),
            new("Wizard", true, "setup-wizard-ready.png", "ready"),
            new("Wizard", true, "setup-wizard-failed.png", "failed"),
            new("BulkOperations", true, "bulk-operations.png"),
            new("Documentation", true, "documentation.png"),
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

            using var provider = BuildProvider();
            ((App)Application.Current!).Services = provider;

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = LogicalWidth,
                Height = LogicalHeight,
            };
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
                Application.Current.RequestedThemeVariant = shot.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
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
                ApplyPostNavigationScenario(vm, shot.Scenario);

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

            if (string.Equals(scenario, "ready", StringComparison.Ordinal)
                || string.Equals(scenario, "failed", StringComparison.Ordinal))
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

        private static void ApplyPostNavigationScenario(MainWindowViewModel mainWindowViewModel, string scenario)
        {
            if (!string.Equals(scenario, "failed", StringComparison.Ordinal)
                || mainWindowViewModel.CurrentViewModel is not WizardViewModel wizard)
            {
                return;
            }

            wizard.Steps[1].IsFailed = true;
            wizard.Steps[1].Result = "Microsoft 365 group could not be created. Retry the step or skip it after checking the output.";
            wizard.CurrentStep = 1;
            wizard.StepFailed = true;
            wizard.StepResult = wizard.Steps[1].Result;
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
