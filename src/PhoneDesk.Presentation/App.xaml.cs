using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk.Localization;
using PhoneDesk.ViewModels;

namespace PhoneDesk;

public partial class App : Application
{
    /// <summary>
    /// The composed DI container, supplied by the composition root (Program.Main).
    /// Used only here to resolve the root ViewModel; Views and ViewModels never touch it.
    /// </summary>
    public IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is not null)
        {
            var translationService = Services.GetRequiredService<ITranslationService>();
            Resources["TranslationCatalog"] = translationService.Text;

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };

            // Dispose the container on exit so IDisposable singletons (e.g. the PowerShell
            // runspace) are released. Replaces MainWindow.OnClosed's manual disposal.
            desktop.ShutdownRequested += (_, _) => (Services as IDisposable)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
