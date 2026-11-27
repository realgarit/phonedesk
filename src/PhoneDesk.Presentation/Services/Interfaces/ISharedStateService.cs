using System.ComponentModel;
using PhoneDesk.Models;

namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Shared state service that provides centralized access to application state.
/// Replaces the pattern of resolving MainWindowViewModel via ApplicationLifetime.
/// </summary>
public interface ISharedStateService : INotifyPropertyChanged
{
    PhoneManagerVariables Variables { get; set; }

    /// <summary>Skip the script preview dialog before executing commands.</summary>
    bool SkipScriptPreview { get; set; }

    /// <summary>Skip the confirmation dialog on destructive (delete/remove) operations.</summary>
    bool SkipDeleteConfirmation { get; set; }

    /// <summary>Automatically refresh lists after create/remove operations.</summary>
    bool AutoRefreshAfterOperations { get; set; }

    /// <summary>Minimum log level displayed in the log viewer.</summary>
    LogLevel MinimumLogLevel { get; set; }

    /// <summary>Guided Setup progress, kept while the operator visits other pages. Null until the setup starts.</summary>
    WizardProgressState? WizardProgress { get; set; }

    /// <summary>The record of the Guided Setup run in progress, kept while the operator visits other pages.</summary>
    PhoneDesk.Operations.RunRecord? SetupRun { get; set; }

    /// <summary>The object a list page asked the Edit Settings page to open. The page takes it and clears it.</summary>
    EditRequest? EditRequest { get; set; }

    /// <summary>The name of the profile in use, for the shell. Empty when no profile is in use.</summary>
    string ActiveProfileLabel { get; set; }

    /// <summary>
    /// The number of sign-ins that are open now (a browser window that has not reported back). A
    /// profile switch is refused while one is open: its late result would connect the customer before.
    /// </summary>
    int OpenSignIns { get; set; }
}

/// <summary>A call queue or an auto attendant to open on the Edit Settings page.</summary>
public sealed record EditRequest(PhoneDesk.Operations.EditObjectKind Kind, string Name);
