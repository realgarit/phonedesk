using CommunityToolkit.Mvvm.ComponentModel;
using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

/// <summary>
/// Shared state service that holds application-wide state.
/// Registered as a singleton so all ViewModels share the same instance.
/// </summary>
public partial class SharedStateService : ObservableObject, ISharedStateService
{
    [ObservableProperty]
    private PhoneManagerVariables _variables = new();

    [ObservableProperty]
    private bool _skipScriptPreview;

    [ObservableProperty]
    private bool _skipDeleteConfirmation;

    [ObservableProperty]
    private bool _autoRefreshAfterOperations = true;

    [ObservableProperty]
    private LogLevel _minimumLogLevel = LogLevel.Info;

    [ObservableProperty]
    private WizardProgressState? _wizardProgress;

    [ObservableProperty]
    private PhoneDesk.Operations.RunRecord? _setupRun;

    [ObservableProperty]
    private EditRequest? _editRequest;

    [ObservableProperty]
    private string _activeProfileLabel = string.Empty;
}
