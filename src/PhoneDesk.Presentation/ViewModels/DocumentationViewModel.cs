using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.ScriptBuilders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.ComponentModel;
using PhoneDesk.Portability;
using PhoneDesk.Topology;

namespace PhoneDesk.ViewModels
{
    public partial class DocumentationViewModel : ViewModelBase, IDisposable
    {
        private readonly IDocumentationScriptBuilder _docBuilder;
        private readonly ITenantAsCodeService? _tenantAsCodeService;
        private readonly IPortabilityFileService? _portabilityFileService;
        private readonly ITenantDocumentationSnapshotParser? _snapshotParser;
        private readonly ITenantTopologyAssembler? _topologyAssembler;
        private IReadOnlyList<TopologyDriftEntry> _topologyDrift = Array.Empty<TopologyDriftEntry>();
        private bool _disposed;

        [ObservableProperty]
        private string _documentationOutput = string.Empty;

        [ObservableProperty]
        private bool _isExporting = false;

        [ObservableProperty]
        private bool _showTopologyDrift;

        [ObservableProperty]
        private string _comparedTopologyFileName = string.Empty;

        public ObservableCollection<TopologyDriftDisplayEntry> TopologyDriftEntries { get; } = new();

        public bool HasTopologyDriftEntries => TopologyDriftEntries.Count > 0;

        // Internal data structures for topology building
        private record RaInfo(string Name, string Upn, string ObjectId, string AppId, string Phone);
        private record AaInfo(string Name, string Identity, string Language, string TimeZone, string Voice, string DefaultFlow);
        private record CqInfo(string Name, string Identity, string Routing, string AlertTime, string Language, int AgentCount, string OverflowThreshold, string TimeoutThreshold, string OverflowAction, string TimeoutAction);
        private record MenuOption(string AaName, string FlowName, string Key, string Action, string TargetId);
        private record CallFlow(string AaName, string FlowName, string MenuName);
        private record ChaInfo(string AaName, string Type, string ScheduleId, string CallFlowId);
        private record AssocInfo(string RaName, string RaId, string ConfigId, string ConfigType);
        private record AgentInfo(string CqName, string ObjectId, string OptIn, string DisplayName, string Upn);
        private record PhoneInfo(string Number, string Type, string AssignedTo, string Status, string Activation, string City, string Capability);
        private record UserInfo(string Name, string Upn, string LineUri, string VoiceRoutingPolicy, string CallingPolicy, string DialPlan);
        private record ScheduleInfo(string Name, string Id, string Type, int DateCount);
        private record ScheduleDateRange(string ScheduleName, string Start, string End);
        private record ScheduleWeekly(string ScheduleName, string Day, string Start, string End);
        private record OverflowTimeout(string CqName, string Action, string TargetId, string Threshold);

        public DocumentationViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            IDocumentationScriptBuilder docBuilder,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null,
            ITenantAsCodeService? tenantAsCodeService = null,
            IPortabilityFileService? portabilityFileService = null,
            ITenantDocumentationSnapshotParser? snapshotParser = null,
            ITenantTopologyAssembler? topologyAssembler = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _docBuilder = docBuilder;
            _tenantAsCodeService = tenantAsCodeService;
            _portabilityFileService = portabilityFileService;
            _snapshotParser = snapshotParser;
            _topologyAssembler = topologyAssembler;
            TopologyDriftEntries.CollectionChanged += (_, _) =>
                OnPropertyChanged(nameof(HasTopologyDriftEntries));
            if (_translationService is not null)
            {
                _translationService.PropertyChanged += OnTranslationServicePropertyChanged;
            }
            LogLocalized(
                UiTextKey.DocumentationPageLoadedLog,
                "Documentation page loaded",
                LogLevel.Info);
        }

        [RelayCommand]
        private async Task ExportTopologySnapshotAsync()
        {
            if (!HasSnapshotServices())
            {
                return;
            }

            try
            {
                IsBusy = true;
                IsExporting = true;
                StatusMessage = GetText(
                    UiTextKey.DocumentationTopologyGatheringStatus,
                    "Reading the complete tenant snapshot...");
                var snapshot = await CollectLiveTopologySnapshotAsync();
                var json = _tenantAsCodeService!.SerializeTopology(snapshot);
                var timestamp = snapshot.RetrievedAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
                var location = await _portabilityFileService!.SaveJsonAsync(
                    GetText(UiTextKey.DocumentationExportTopologySnapshotAction, "Export topology snapshot"),
                    $"phonedesk-topology-{timestamp}.json",
                    json);
                if (location is null)
                {
                    return;
                }

                StatusMessage = GetText(
                    UiTextKey.DocumentationTopologySnapshotSavedStatus,
                    "Topology snapshot saved to {path}.",
                    new Dictionary<string, object?> { ["path"] = location });
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.DocumentationTopologySnapshotFailedStatus,
                    "Topology snapshot failed: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
            finally
            {
                IsBusy = false;
                IsExporting = false;
            }
        }

        [RelayCommand]
        private async Task CompareTopologySnapshotAsync()
        {
            if (!HasSnapshotServices())
            {
                return;
            }

            try
            {
                var file = await _portabilityFileService!.OpenJsonAsync(
                    GetText(UiTextKey.DocumentationCompareTopologySnapshotAction, "Compare topology snapshot"));
                if (file is null)
                {
                    return;
                }

                var snapshot = _tenantAsCodeService!.DeserializeTopology(file.Content);
                IsBusy = true;
                IsExporting = true;
                StatusMessage = GetText(
                    UiTextKey.DocumentationTopologyGatheringStatus,
                    "Reading the complete tenant snapshot...");
                var liveSnapshot = await CollectLiveTopologySnapshotAsync();
                var drift = _tenantAsCodeService.CompareTopology(snapshot, liveSnapshot);
                SetTopologyDriftForDisplay(file.Name, drift);
                StatusMessage = drift.Count == 0
                    ? GetText(UiTextKey.DocumentationTopologyNoDriftStatus, "No topology drift detected.")
                    : GetText(
                        UiTextKey.DocumentationTopologyDriftCountStatus,
                        "Detected {count} topology change(s).",
                        new Dictionary<string, object?> { ["count"] = drift.Count });
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.DocumentationTopologyComparisonFailedStatus,
                    "Topology comparison failed: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
            finally
            {
                IsBusy = false;
                IsExporting = false;
            }
        }

        [RelayCommand]
        private void ClearTopologyDrift()
        {
            TopologyDriftEntries.Clear();
            _topologyDrift = Array.Empty<TopologyDriftEntry>();
            OnPropertyChanged(nameof(HasTopologyDriftEntries));
            ComparedTopologyFileName = string.Empty;
            ShowTopologyDrift = false;
        }

        public void SetTopologyDriftForDisplay(
            string fileName,
            IEnumerable<TopologyDriftEntry> entries)
        {
            _topologyDrift = entries.ToArray();
            RefreshTopologyDriftLabels();
            ComparedTopologyFileName = fileName;
            ShowTopologyDrift = true;
        }

        private void RefreshTopologyDriftLabels()
        {
            TopologyDriftEntries.Clear();
            foreach (var entry in _topologyDrift)
            {
                var label = entry.Kind switch
                {
                    TopologyDriftKind.Added => GetText(UiTextKey.DocumentationTopologyAddedValue, "Added"),
                    TopologyDriftKind.Removed => GetText(UiTextKey.DocumentationTopologyRemovedValue, "Removed"),
                    _ => GetText(UiTextKey.DocumentationTopologyChangedValue, "Changed"),
                };
                TopologyDriftEntries.Add(new TopologyDriftDisplayEntry(entry, label));
            }
        }

        private void OnTranslationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ITranslationService.CurrentLanguage) && _topologyDrift.Count > 0)
            {
                RefreshTopologyDriftLabels();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            if (_translationService is not null)
            {
                _translationService.PropertyChanged -= OnTranslationServicePropertyChanged;
            }
            _disposed = true;
        }

        public sealed record TopologyDriftDisplayEntry(TopologyDriftEntry Entry, string ChangeLabel)
        {
            public TopologyDriftKind Kind => Entry.Kind;
            public string ObjectType => Entry.ObjectType;
            public string ObjectId => Entry.ObjectId;
            public string DisplayName => Entry.DisplayName;
            public string? PropertyName => Entry.PropertyName;
            public string? SnapshotValue => Entry.SnapshotValue;
            public string? LiveValue => Entry.LiveValue;
        }

        private bool HasSnapshotServices()
        {
            if (_tenantAsCodeService is not null && _portabilityFileService is not null &&
                _snapshotParser is not null && _topologyAssembler is not null)
            {
                return true;
            }

            StatusMessage = GetText(
                UiTextKey.DocumentationTopologyServicesUnavailableStatus,
                "Tenant snapshot services are unavailable.");
            return false;
        }

        private async Task<TopologySnapshotDocument> CollectLiveTopologySnapshotAsync()
        {
            var tenant = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportTenantInfoCommand(),
                "SnapshotTenantInfo");
            var resourceAccounts = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportResourceAccountsCommand(),
                "SnapshotResourceAccounts");
            var autoAttendants = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportAutoAttendantsCommand(),
                "SnapshotAutoAttendants");
            var callQueues = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportCallQueuesCommand(),
                "SnapshotCallQueues");
            var schedules = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportSchedulesCommand(),
                "SnapshotSchedules");
            var phoneNumbers = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportPhoneNumbersCommand(),
                "SnapshotPhoneNumbers");
            var voiceUsers = await ExecuteCompleteSnapshotReadAsync(
                _docBuilder.GetExportVoiceUsersCommand(),
                "SnapshotVoiceUsers");
            var topologyRaw = await ExecuteCompleteSnapshotReadAsync(
                _powerShellCommandService.GetRetrieveTenantTopologyCommand(),
                "SnapshotTopology");
            if (!topologyRaw.Contains("SUCCESS: Tenant topology retrieved", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The live tenant topology query did not emit its completion marker.");
            }

            var retrievedAtUtc = DateTimeOffset.UtcNow;
            var documentation = _snapshotParser!.Parse(new TenantDocumentationRawData(
                tenant,
                resourceAccounts,
                autoAttendants,
                callQueues,
                schedules,
                phoneNumbers,
                voiceUsers));
            var topology = _topologyAssembler!.Assemble(topologyRaw, retrievedAtUtc);
            return _tenantAsCodeService!.CreateTopologySnapshot(topology, documentation);
        }

        private async Task<string> ExecuteCompleteSnapshotReadAsync(string command, string context)
        {
            var result = await ExecutePowerShellCommandAsync(
                command,
                environmentVariables: null,
                context,
                allowThrottleRetry: true);
            if (!result.IsSuccess)
            {
                throw new InvalidDataException(
                    result.ErrorMessage ?? $"The {context} query did not complete successfully.");
            }

            var output = result.Value ?? string.Empty;
            var diagnostic = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line =>
                    line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("TOPERR:", StringComparison.OrdinalIgnoreCase));
            if (diagnostic is not null)
            {
                throw new InvalidDataException(
                    $"The live tenant snapshot is incomplete: {diagnostic}");
            }
            return output;
        }

        [RelayCommand]
        private async Task ExportDocumentationAsync()
        {
            try
            {
                IsBusy = true;
                IsExporting = true;
                StatusMessage = GetText(
                    UiTextKey.DocumentationGatheringStatus,
                    "Gathering tenant documentation...");

                // Collect all data
                var raList = new List<RaInfo>();
                var aaList = new List<AaInfo>();
                var cqList = new List<CqInfo>();
                var menuOptions = new List<MenuOption>();
                var callFlows = new List<CallFlow>();
                var chaList = new List<ChaInfo>();
                var assocList = new List<AssocInfo>();
                var agentList = new List<AgentInfo>();
                var phoneList = new List<PhoneInfo>();
                var userList = new List<UserInfo>();
                var schedList = new List<ScheduleInfo>();
                var schedDateRanges = new List<ScheduleDateRange>();
                var schedWeekly = new List<ScheduleWeekly>();
                var overflowList = new List<OverflowTimeout>();
                var timeoutList = new List<OverflowTimeout>();
                var operatorList = new List<(string AaName, string OpType, string OpId)>();
                var dlList = new List<(string CqName, string DlId)>();
                string tenantName = "", tenantId = "", tenantCountry = "", tenantLang = "";

                // 1. Tenant Info
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "tenant info", ["current"] = 1, ["total"] = 7 });
                var tenantResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportTenantInfoCommand(), null, "ExportTenantInfo", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(tenantResult.Value))
                {
                    foreach (var line in tenantResult.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("DOCDATA_TENANT:") && line.Length > 15)
                        {
                            var parts = line.Substring(15).Split('|');
                            if (parts.Length >= 4)
                            {
                                tenantName = parts[0].Trim();
                                tenantId = parts[1].Trim();
                                tenantCountry = parts[2].Trim();
                                tenantLang = parts[3].Trim();
                            }
                        }
                    }
                }

                // 2. Resource Accounts + Associations
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "resource accounts", ["current"] = 2, ["total"] = 7 });
                var raResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportResourceAccountsCommand(), null, "ExportResourceAccounts", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(raResult.Value))
                    ParseResourceAccountData(raResult.Value, raList, assocList);

                // 3. Auto Attendants
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "auto attendants", ["current"] = 3, ["total"] = 7 });
                var aaResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportAutoAttendantsCommand(), null, "ExportAutoAttendants", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(aaResult.Value))
                    ParseAutoAttendantData(aaResult.Value, aaList, menuOptions, callFlows, chaList, operatorList);

                // 4. Call Queues
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "call queues", ["current"] = 4, ["total"] = 7 });
                var cqResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportCallQueuesCommand(), null, "ExportCallQueues", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(cqResult.Value))
                    ParseCallQueueData(cqResult.Value, cqList, agentList, overflowList, timeoutList, dlList);

                // 5. Schedules
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "schedules", ["current"] = 5, ["total"] = 7 });
                var schedResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportSchedulesCommand(), null, "ExportSchedules", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(schedResult.Value))
                    ParseScheduleData(schedResult.Value, schedList, schedDateRanges, schedWeekly);

                // 6. Phone Numbers
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "phone numbers", ["current"] = 6, ["total"] = 7 });
                var phoneResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportPhoneNumbersCommand(), null, "ExportPhoneNumbers", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(phoneResult.Value))
                    ParsePhoneData(phoneResult.Value, phoneList);

                // 7. Voice Users
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportStepStatus,
                    "Exporting {item}... ({current}/{total})",
                    new Dictionary<string, object?> { ["item"] = "voice users", ["current"] = 7, ["total"] = 7 });
                var userResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportVoiceUsersCommand(), null, "ExportVoiceUsers", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(userResult.Value))
                    ParseUserData(userResult.Value, userList);

                // Build the comprehensive documentation
                StatusMessage = GetText(
                    UiTextKey.DocumentationBuildingStatus,
                    "Building documentation...");
                var doc = new StringBuilder();
                BuildDocumentation(doc, tenantName, tenantId, tenantCountry, tenantLang,
                    raList, aaList, cqList, menuOptions, callFlows, chaList, assocList,
                    agentList, phoneList, userList, schedList, schedDateRanges, schedWeekly,
                    overflowList, timeoutList, operatorList, dlList);

                DocumentationOutput = doc.ToString();
                StatusMessage = GetText(
                    UiTextKey.DocumentationExportSuccess,
                    "Documentation exported successfully. You can copy the text above.");
                LogLocalized(
                    UiTextKey.DocumentationExportSuccessLog,
                    "Documentation exported successfully",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                StatusMessage = FormatError(ex.Message);
                LogLocalized(
                    UiTextKey.RuntimeExceptionLog,
                    "Exception in {context}: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["context"] = nameof(ExportDocumentationAsync), ["details"] = ex.ToString() });
            }
            finally
            {
                IsBusy = false;
                IsExporting = false;
            }
        }

        [RelayCommand]
        private async Task CopyToClipboardAsync()
        {
            if (string.IsNullOrEmpty(DocumentationOutput))
            {
                StatusMessage = GetText(
                    UiTextKey.DocumentationNothingToCopyError,
                    "No documentation to copy. Export first.");
                return;
            }

            try
            {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                if (topLevel != null)
                {
                    var clipboard = topLevel.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(DocumentationOutput);
                        StatusMessage = GetText(
                            UiTextKey.DocumentationCopiedStatus,
                            "Documentation copied to clipboard.");
                        LogLocalized(
                            UiTextKey.DocumentationCopiedLog,
                            "Documentation copied to clipboard",
                            LogLevel.Info);
                        return;
                    }
                }
                StatusMessage = GetText(
                    UiTextKey.DocumentationClipboardUnavailable,
                    "Clipboard not available.");
            }
            catch (Exception ex)
            {
                StatusMessage = GetText(
                    UiTextKey.DocumentationCopyFailedStatus,
                    "Failed to copy: {error}",
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                LogLocalized(
                    UiTextKey.DocumentationCopyFailedLog,
                    "Failed to copy documentation to clipboard: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        // ─────────────────────── DATA PARSING ───────────────────────

        private void ParseResourceAccountData(string result, List<RaInfo> raList, List<AssocInfo> assocList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inRa = false, inAssoc = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_RA_START")) { inRa = true; continue; }
                if (line.Contains("DOCDATA_RA_END")) { inRa = false; continue; }
                if (line.Contains("DOCDATA_ASSOC_START")) { inAssoc = true; continue; }
                if (line.Contains("DOCDATA_ASSOC_END")) { inAssoc = false; continue; }

                if (inRa && line.StartsWith("DOCDATA_RA:"))
                {
                    var p = line.Substring(11).Split('|');
                    if (p.Length >= 5)
                        raList.Add(new RaInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim()));
                }
                if (inAssoc && line.StartsWith("DOCDATA_ASSOC:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 4)
                        assocList.Add(new AssocInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim()));
                }
            }
        }

        private void ParseAutoAttendantData(string result, List<AaInfo> aaList, List<MenuOption> menuOptions, List<CallFlow> callFlows, List<ChaInfo> chaList, List<(string, string, string)> operatorList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_AA_START")) { inSection = true; continue; }
                if (line.Contains("DOCDATA_AA_END")) { inSection = false; continue; }
                if (!inSection) continue;

                if (line.StartsWith("DOCDATA_AA:"))
                {
                    var p = line.Substring(11).Split('|');
                    if (p.Length >= 6)
                        aaList.Add(new AaInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(), p[5].Trim()));
                }
                else if (line.StartsWith("DOCDATA_AA_MENU:"))
                {
                    var p = line.Substring(16).Split('|');
                    if (p.Length >= 5)
                        menuOptions.Add(new MenuOption(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim()));
                }
                else if (line.StartsWith("DOCDATA_AA_CF:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 3)
                        callFlows.Add(new CallFlow(p[0].Trim(), p[1].Trim(), p[2].Trim()));
                }
                else if (line.StartsWith("DOCDATA_AA_CHA:"))
                {
                    var p = line.Substring(15).Split('|');
                    if (p.Length >= 4)
                        chaList.Add(new ChaInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim()));
                }
                else if (line.StartsWith("DOCDATA_AA_OP:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 3)
                        operatorList.Add((p[0].Trim(), p[1].Trim(), p[2].Trim()));
                }
            }
        }

        private void ParseCallQueueData(string result, List<CqInfo> cqList, List<AgentInfo> agentList, List<OverflowTimeout> overflowList, List<OverflowTimeout> timeoutList, List<(string, string)> dlList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_CQ_START")) { inSection = true; continue; }
                if (line.Contains("DOCDATA_CQ_END")) { inSection = false; continue; }
                if (!inSection) continue;

                if (line.StartsWith("DOCDATA_CQ:"))
                {
                    var p = line.Substring(11).Split('|');
                    if (p.Length >= 10)
                        cqList.Add(new CqInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(),
                            int.TryParse(p[5].Trim(), out var c) ? c : 0, p[6].Trim(), p[7].Trim(), p[8].Trim(), p[9].Trim()));
                }
                else if (line.StartsWith("DOCDATA_CQ_AGENT:"))
                {
                    var p = line.Substring(17).Split('|');
                    if (p.Length >= 5)
                        agentList.Add(new AgentInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim()));
                    else if (p.Length >= 3)
                        agentList.Add(new AgentInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[1].Trim(), ""));
                }
                else if (line.StartsWith("DOCDATA_CQ_OVERFLOW:"))
                {
                    var p = line.Substring(20).Split('|');
                    if (p.Length >= 4)
                        overflowList.Add(new OverflowTimeout(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim()));
                }
                else if (line.StartsWith("DOCDATA_CQ_TIMEOUT:"))
                {
                    var p = line.Substring(19).Split('|');
                    if (p.Length >= 4)
                        timeoutList.Add(new OverflowTimeout(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim()));
                }
                else if (line.StartsWith("DOCDATA_CQ_DL:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 2)
                        dlList.Add((p[0].Trim(), p[1].Trim()));
                }
            }
        }

        private void ParseScheduleData(string result, List<ScheduleInfo> schedList, List<ScheduleDateRange> dateRanges, List<ScheduleWeekly> weeklyList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_SCHED_START")) { inSection = true; continue; }
                if (line.Contains("DOCDATA_SCHED_END")) { inSection = false; continue; }
                if (!inSection) continue;

                if (line.StartsWith("DOCDATA_SCHED:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 4)
                        schedList.Add(new ScheduleInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), int.TryParse(p[3].Trim(), out var c) ? c : 0));
                }
                else if (line.StartsWith("DOCDATA_SCHED_DR:"))
                {
                    var p = line.Substring(17).Split('|');
                    if (p.Length >= 3)
                        dateRanges.Add(new ScheduleDateRange(p[0].Trim(), p[1].Trim(), p[2].Trim()));
                }
                else if (line.StartsWith("DOCDATA_SCHED_WK:"))
                {
                    var p = line.Substring(17).Split('|');
                    if (p.Length >= 4)
                        weeklyList.Add(new ScheduleWeekly(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim()));
                }
            }
        }

        private void ParsePhoneData(string result, List<PhoneInfo> phoneList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_PHONE_START")) { inSection = true; continue; }
                if (line.Contains("DOCDATA_PHONE_END")) { inSection = false; continue; }
                if (inSection && line.StartsWith("DOCDATA_PHONE:"))
                {
                    var p = line.Substring(14).Split('|');
                    if (p.Length >= 7)
                        phoneList.Add(new PhoneInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(), p[5].Trim(), p[6].Trim()));
                }
            }
        }

        private void ParseUserData(string result, List<UserInfo> userList)
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Contains("DOCDATA_USER_START")) { inSection = true; continue; }
                if (line.Contains("DOCDATA_USER_END")) { inSection = false; continue; }
                if (inSection && line.StartsWith("DOCDATA_USER:"))
                {
                    var p = line.Substring(13).Split('|');
                    if (p.Length >= 6)
                        userList.Add(new UserInfo(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(), p[5].Trim()));
                }
            }
        }

        // ─────────────────────── DOCUMENT BUILDING ───────────────────────

        private void BuildDocumentation(StringBuilder doc,
            string tenantName, string tenantId, string tenantCountry, string tenantLang,
            List<RaInfo> raList, List<AaInfo> aaList, List<CqInfo> cqList,
            List<MenuOption> menuOptions, List<CallFlow> callFlows, List<ChaInfo> chaList,
            List<AssocInfo> assocList, List<AgentInfo> agentList, List<PhoneInfo> phoneList,
            List<UserInfo> userList, List<ScheduleInfo> schedList,
            List<ScheduleDateRange> schedDateRanges, List<ScheduleWeekly> schedWeekly,
            List<OverflowTimeout> overflowList, List<OverflowTimeout> timeoutList,
            List<(string AaName, string OpType, string OpId)> operatorList,
            List<(string CqName, string DlId)> dlList)
        {
            // ──── HEADER ────
            doc.AppendLine("═══════════════════════════════════════════════════════════════");
            AppendReportLine(doc, UiTextKey.DocumentationReportTitle, "  TEAMS PHONE SYSTEM — COMPLETE TENANT DOCUMENTATION");
            doc.AppendLine("═══════════════════════════════════════════════════════════════");
            AppendReportLine(
                doc,
                UiTextKey.DocumentationReportGeneratedLabel,
                "  Generated: {time}",
                new Dictionary<string, object?> { ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            doc.AppendLine();

            // ──── 1. TENANT INFO ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportTenantInformationTitle, "│  1. TENANT INFORMATION                                      │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (!string.IsNullOrEmpty(tenantName))
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportTenantNameLabel, "  Tenant Name:    {value}", new Dictionary<string, object?> { ["value"] = tenantName });
                AppendReportLine(doc, UiTextKey.DocumentationReportTenantIdLabel, "  Tenant ID:      {value}", new Dictionary<string, object?> { ["value"] = tenantId });
                AppendReportLine(doc, UiTextKey.DocumentationReportCountryLabel, "  Country:        {value}", new Dictionary<string, object?> { ["value"] = tenantCountry });
                AppendReportLine(doc, UiTextKey.DocumentationReportLanguageLabel, "  Language:       {value}", new Dictionary<string, object?> { ["value"] = tenantLang });
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportTenantInfoUnavailable, "  (Tenant information not available)");
            }
            doc.AppendLine();

            // ──── 2. EXECUTIVE SUMMARY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportExecutiveSummaryTitle, "│  2. EXECUTIVE SUMMARY                                       │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            AppendReportLine(doc, UiTextKey.DocumentationReportAutoAttendantsCount, "  Auto Attendants:    {count}", new Dictionary<string, object?> { ["count"] = aaList.Count });
            AppendReportLine(doc, UiTextKey.DocumentationReportCallQueuesCount, "  Call Queues:        {count}", new Dictionary<string, object?> { ["count"] = cqList.Count });
            AppendReportLine(doc, UiTextKey.DocumentationReportResourceAccountsCount, "  Resource Accounts:  {count}", new Dictionary<string, object?> { ["count"] = raList.Count });
            AppendReportLine(doc, UiTextKey.DocumentationReportPhoneNumbersCount, "  Phone Numbers:      {count}", new Dictionary<string, object?> { ["count"] = phoneList.Count });
            AppendReportLine(doc, UiTextKey.DocumentationReportVoiceUsersCount, "  Voice Users:        {count}", new Dictionary<string, object?> { ["count"] = userList.Count });
            AppendReportLine(doc, UiTextKey.DocumentationReportSchedulesCount, "  Schedules:          {count}", new Dictionary<string, object?> { ["count"] = schedList.Count });
            var assignedNumbers = phoneList.Count(p => !string.IsNullOrEmpty(p.Status) && p.Status.Contains("Assign", StringComparison.OrdinalIgnoreCase));
            var unassigned = phoneList.Count - assignedNumbers;
            AppendReportLine(doc, UiTextKey.DocumentationReportNumbersAssigned, "  Numbers Assigned:   {count}", new Dictionary<string, object?> { ["count"] = assignedNumbers });
            AppendReportLine(doc, UiTextKey.DocumentationReportNumbersUnassigned, "  Numbers Unassigned: {count}", new Dictionary<string, object?> { ["count"] = unassigned });
            doc.AppendLine();

            // ──── 3. CALL ROUTING TOPOLOGY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportCallRoutingTopologyTitle, "│  3. CALL ROUTING TOPOLOGY                                   │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            BuildTopology(doc, raList, aaList, cqList, menuOptions, assocList, agentList, overflowList, timeoutList, operatorList);

            // ──── 4. PHONE NUMBER INVENTORY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportPhoneNumberInventoryTitle, "│  4. PHONE NUMBER INVENTORY                                  │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (phoneList.Count > 0)
            {
                // Build RA lookup by ObjectId for resolving names
                var raById = raList.ToDictionary(r => r.ObjectId, r => r, StringComparer.OrdinalIgnoreCase);
                var userByUpn = userList.ToDictionary(u => u.Upn, u => u, StringComparer.OrdinalIgnoreCase);

                AppendReportLine(doc, UiTextKey.DocumentationReportPhoneNumberTableHeader, "  Number               │ Type     │ Status     │ Assigned To               │ City");
                doc.AppendLine("  ─────────────────────┼──────────┼────────────┼───────────────────────────┼────────────");
                foreach (var p in phoneList.OrderBy(x => x.Number))
                {
                    var assignedName = p.AssignedTo;
                    if (!string.IsNullOrEmpty(p.AssignedTo))
                    {
                        if (raById.TryGetValue(p.AssignedTo, out var ra))
                            assignedName = $"{ra.Name} (RA)";
                    }
                    doc.AppendLine($"  {p.Number,-21} │ {p.Type,-8} │ {p.Status,-10} │ {assignedName,-25} │ {p.City}");
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoPhoneNumbers, "  (No phone numbers found or insufficient permissions)");
            }
            doc.AppendLine();

            // ──── 5. RESOURCE ACCOUNTS ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportResourceAccountsTitle, "│  5. RESOURCE ACCOUNTS                                       │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (raList.Count > 0)
            {
                foreach (var ra in raList.OrderBy(r => r.Name))
                {
                    var appType = ra.AppId switch
                    {
                        "11cd3e2e-fccb-42ad-ad00-878b93575e07" => GetText(UiTextKey.DocumentationReportCallQueueType, "Call Queue"),
                        "ce933385-9390-45d1-9512-c8d228074e07" => GetText(UiTextKey.DocumentationReportAutoAttendantType, "Auto Attendant"),
                        _ => ra.AppId
                    };
                    var assoc = assocList.FirstOrDefault(a => a.RaId.Equals(ra.ObjectId, StringComparison.OrdinalIgnoreCase));
                    var assocTarget = assoc != null
                        ? $"→ {assoc.ConfigType}: {assoc.ConfigId}"
                        : GetText(UiTextKey.DocumentationReportNotAssociatedValue, "(not associated)");

                    doc.AppendLine($"  ● {ra.Name}");
                    AppendReportLine(doc, UiTextKey.DocumentationReportUpnField, "    UPN:         {value}", new Dictionary<string, object?> { ["value"] = ra.Upn });
                    AppendReportLine(doc, UiTextKey.DocumentationReportTypeField, "    Type:        {value}", new Dictionary<string, object?> { ["value"] = appType });
                    AppendReportLine(doc, UiTextKey.DocumentationReportPhoneField, "    Phone:       {value}", new Dictionary<string, object?> { ["value"] = string.IsNullOrEmpty(ra.Phone) ? GetText(UiTextKey.DocumentationReportNoneValue, "(none)") : ra.Phone });
                    AppendReportLine(doc, UiTextKey.DocumentationReportAssociationField, "    Association: {value}", new Dictionary<string, object?> { ["value"] = assocTarget });
                    doc.AppendLine();
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoResourceAccounts, "  (No resource accounts found)");
                doc.AppendLine();
            }

            // ──── 6. AUTO ATTENDANTS (DETAILED) ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportAutoAttendantsTitle, "│  6. AUTO ATTENDANTS — DETAILED CONFIGURATION                │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (aaList.Count > 0)
            {
                foreach (var aa in aaList.OrderBy(a => a.Name))
                {
                    doc.AppendLine($"  ╔═ {aa.Name} ═══════════════════════════════════════");
                    AppendReportLine(doc, UiTextKey.DocumentationReportLanguageField, "  ║  Language:  {value}", new Dictionary<string, object?> { ["value"] = aa.Language });
                    AppendReportLine(doc, UiTextKey.DocumentationReportTimeZoneField, "  ║  TimeZone:  {value}", new Dictionary<string, object?> { ["value"] = aa.TimeZone });
                    AppendReportLine(doc, UiTextKey.DocumentationReportVoiceField, "  ║  Voice:     {value}", new Dictionary<string, object?> { ["value"] = aa.Voice });
                    AppendReportLine(doc, UiTextKey.DocumentationReportDefaultField, "  ║  Default:   {value}", new Dictionary<string, object?> { ["value"] = aa.DefaultFlow });

                    // Operator
                    var op = operatorList.FirstOrDefault(o => o.AaName == aa.Name);
                    if (!string.IsNullOrEmpty(op.AaName))
                    {
                        var opName = ResolveTargetName(op.OpId, raList, aaList, cqList);
                        AppendReportLine(doc, UiTextKey.DocumentationReportOperatorField, "  ║  Operator:  {name} ({type})", new Dictionary<string, object?> { ["name"] = opName, ["type"] = op.OpType });
                    }

                    // Call Flows
                    var flows = callFlows.Where(f => f.AaName == aa.Name).ToList();
                    if (flows.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportCallFlowsLabel, "  ║  Call Flows:");
                        foreach (var cf in flows)
                        {
                            AppendReportLine(doc, UiTextKey.DocumentationReportCallFlowEntry, "  ║    ├─ {flow} (Menu: {menu})", new Dictionary<string, object?> { ["flow"] = cf.FlowName, ["menu"] = cf.MenuName });
                            var opts = menuOptions.Where(m => m.AaName == aa.Name && m.FlowName == cf.FlowName).ToList();
                            foreach (var opt in opts)
                            {
                                var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                                AppendReportLine(doc, UiTextKey.DocumentationReportKeyActionEntry, "  ║    │    Key {key} → {action}: {target}", new Dictionary<string, object?> { ["key"] = opt.Key, ["action"] = opt.Action, ["target"] = targetName });
                            }
                        }
                    }

                    // Default call flow menu options
                    var defaultOpts = menuOptions.Where(m => m.AaName == aa.Name && m.FlowName == "DefaultCallFlow").ToList();
                    if (defaultOpts.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportDefaultMenuOptionsLabel, "  ║  Default Menu Options:");
                        foreach (var opt in defaultOpts)
                        {
                            var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                            AppendReportLine(doc, UiTextKey.DocumentationReportKeyActionEntry, "  ║    │    Key {key} → {action}: {target}", new Dictionary<string, object?> { ["key"] = opt.Key, ["action"] = opt.Action, ["target"] = targetName });
                        }
                    }

                    // Schedule associations
                    var chas = chaList.Where(c => c.AaName == aa.Name).ToList();
                    if (chas.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportScheduleAssociationsLabel, "  ║  Schedule Associations:");
                        foreach (var cha in chas)
                        {
                            AppendReportLine(doc, UiTextKey.DocumentationReportScheduleAssociationEntry, "  ║    {type}: Schedule={schedule}, CallFlow={callFlow}", new Dictionary<string, object?> { ["type"] = cha.Type, ["schedule"] = cha.ScheduleId, ["callFlow"] = cha.CallFlowId });
                        }
                    }

                    doc.AppendLine($"  ╚═══════════════════════════════════════════════════════");
                    doc.AppendLine();
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoAutoAttendants, "  (No auto attendants found)");
                doc.AppendLine();
            }

            // ──── 7. CALL QUEUES (DETAILED) ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportCallQueuesTitle, "│  7. CALL QUEUES — DETAILED CONFIGURATION                    │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (cqList.Count > 0)
            {
                foreach (var cq in cqList.OrderBy(c => c.Name))
                {
                    doc.AppendLine($"  ╔═ {cq.Name} ═══════════════════════════════════════");
                    AppendReportLine(doc, UiTextKey.DocumentationReportRoutingField, "  ║  Routing:      {value}", new Dictionary<string, object?> { ["value"] = cq.Routing });
                    AppendReportLine(doc, UiTextKey.DocumentationReportAlertTimeField, "  ║  Alert Time:   {value}s", new Dictionary<string, object?> { ["value"] = cq.AlertTime });
                    AppendReportLine(doc, UiTextKey.DocumentationReportLanguageField, "  ║  Language:  {value}", new Dictionary<string, object?> { ["value"] = cq.Language });
                    AppendReportLine(doc, UiTextKey.DocumentationReportAgentsField, "  ║  Agents:       {count}", new Dictionary<string, object?> { ["count"] = cq.AgentCount });

                    // Agent IDs
                    var agents = agentList.Where(a => a.CqName == cq.Name).ToList();
                    if (agents.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportAgentListLabel, "  ║  Agent List (✓ = opted in, ✗ = opted out):");
                        foreach (var agent in agents)
                        {
                            var optIn = agent.OptIn.Equals("True", StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
                            var agentLabel = !string.IsNullOrWhiteSpace(agent.Upn)
                                ? $"{agent.DisplayName} ({agent.Upn})"
                                : !string.IsNullOrWhiteSpace(agent.DisplayName) && agent.DisplayName != agent.ObjectId
                                    ? agent.DisplayName
                                    : agent.ObjectId;
                            doc.AppendLine($"  ║    {optIn}  {agentLabel}");
                        }
                    }

                    // Distribution lists
                    var dls = dlList.Where(d => d.CqName == cq.Name).ToList();
                    if (dls.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportDistributionListsLabel, "  ║  Distribution Lists:");
                        foreach (var dl in dls)
                            doc.AppendLine($"  ║    • {dl.DlId}");
                    }

                    // Overflow
                    var of = overflowList.FirstOrDefault(o => o.CqName == cq.Name);
                    if (of != null)
                    {
                        var targetName = ResolveTargetName(of.TargetId, raList, aaList, cqList);
                        doc.AppendLine($"  ║");
                        AppendReportLine(doc, UiTextKey.DocumentationReportOverflowLine, "  ║  Overflow:     {action} (threshold >{threshold}) → {target}", new Dictionary<string, object?> { ["action"] = of.Action, ["threshold"] = of.Threshold, ["target"] = targetName });
                    }
                    else
                    {
                        AppendReportLine(doc, UiTextKey.DocumentationReportOverflowLineNoTarget, "  ║  Overflow:     {action} (threshold >{threshold})", new Dictionary<string, object?> { ["action"] = cq.OverflowAction, ["threshold"] = cq.OverflowThreshold });
                    }

                    // Timeout
                    var to = timeoutList.FirstOrDefault(t => t.CqName == cq.Name);
                    if (to != null)
                    {
                        var targetName = ResolveTargetName(to.TargetId, raList, aaList, cqList);
                        AppendReportLine(doc, UiTextKey.DocumentationReportTimeoutLine, "  ║  Timeout:      {action} (threshold >{threshold}s) → {target}", new Dictionary<string, object?> { ["action"] = to.Action, ["threshold"] = to.Threshold, ["target"] = targetName });
                    }
                    else
                    {
                        AppendReportLine(doc, UiTextKey.DocumentationReportTimeoutLineNoTarget, "  ║  Timeout:      {action} (threshold >{threshold}s)", new Dictionary<string, object?> { ["action"] = cq.TimeoutAction, ["threshold"] = cq.TimeoutThreshold });
                    }

                    doc.AppendLine($"  ╚═══════════════════════════════════════════════════════");
                    doc.AppendLine();
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoCallQueues, "  (No call queues found)");
                doc.AppendLine();
            }

            // ──── 8. SCHEDULES ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportSchedulesTitle, "│  8. SCHEDULES (HOLIDAYS & BUSINESS HOURS)                   │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (schedList.Count > 0)
            {
                foreach (var sched in schedList.OrderBy(s => s.Name))
                {
                    doc.AppendLine($"  ● {sched.Name}  [{sched.Type}]");

                    // Fixed schedule date ranges
                    var ranges = schedDateRanges.Where(d => d.ScheduleName == sched.Name).ToList();
                    if (ranges.Count > 0)
                    {
                        AppendReportLine(doc, UiTextKey.DocumentationReportHolidayDatesLabel, "    Holiday Dates:");
                        foreach (var r in ranges)
                            doc.AppendLine($"      {r.Start}  →  {r.End}");
                    }

                    // Weekly schedule
                    var weekly = schedWeekly.Where(w => w.ScheduleName == sched.Name).ToList();
                    if (weekly.Count > 0)
                    {
                        AppendReportLine(doc, UiTextKey.DocumentationReportWeeklyHoursLabel, "    Weekly Hours:");
                        foreach (var w in weekly)
                            doc.AppendLine($"      {w.Day,-10}  {w.Start} - {w.End}");
                    }
                    doc.AppendLine();
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoSchedules, "  (No schedules found)");
                doc.AppendLine();
            }

            // ──── 9. VOICE-ENABLED USERS ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            AppendReportLine(doc, UiTextKey.DocumentationReportVoiceEnabledUsersTitle, "│  9. VOICE-ENABLED USERS                                     │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (userList.Count > 0)
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportVoiceUsersTableHeader, "  Name                        │ UPN                          │ Phone          │ VRP        │ Calling Policy");
                doc.AppendLine("  ────────────────────────────┼──────────────────────────────┼────────────────┼────────────┼──────────────");
                foreach (var u in userList.OrderBy(x => x.Name))
                {
                    doc.AppendLine($"  {u.Name,-28} │ {u.Upn,-28} │ {u.LineUri,-14} │ {u.VoiceRoutingPolicy,-10} │ {u.CallingPolicy}");
                }
            }
            else
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoVoiceUsers, "  (No voice-enabled users found or insufficient permissions)");
            }
            doc.AppendLine();

            // ──── 10. CURRENT CONFIGURATION ────
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer))
            {
                doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
                AppendReportLine(doc, UiTextKey.DocumentationReportCurrentConfigurationTitle, "│  10. CURRENT PHONE MANAGER CONFIGURATION                    │");
                doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
                doc.AppendLine();
                AppendReportLine(doc, UiTextKey.DocumentationReportCustomerLabel, "  Customer:        {value}", new Dictionary<string, object?> { ["value"] = variables.Customer });
                AppendReportLine(doc, UiTextKey.DocumentationReportCustomerGroupLabel, "  Customer Group:  {value}", new Dictionary<string, object?> { ["value"] = variables.CustomerGroupName });
                AppendReportLine(doc, UiTextKey.DocumentationReportLanguageLabel, "  Language:       {value}", new Dictionary<string, object?> { ["value"] = variables.LanguageId });
                AppendReportLine(doc, UiTextKey.DocumentationReportTimeZoneLabel, "  Time Zone:       {value}", new Dictionary<string, object?> { ["value"] = variables.TimeZoneId });
                AppendReportLine(doc, UiTextKey.DocumentationReportUsageLocationLabel, "  Usage Location:  {value}", new Dictionary<string, object?> { ["value"] = variables.UsageLocation });
                AppendReportLine(doc, UiTextKey.DocumentationReportM365GroupLabel, "  M365 Group:      {value}", new Dictionary<string, object?> { ["value"] = variables.M365Group });
                AppendReportLine(doc, UiTextKey.DocumentationReportCallQueueDisplayNameLabel, "  CQ Display Name: {value}", new Dictionary<string, object?> { ["value"] = variables.CqDisplayName });
                AppendReportLine(doc, UiTextKey.DocumentationReportAutoAttendantDisplayNameLabel, "  AA Display Name: {value}", new Dictionary<string, object?> { ["value"] = variables.AaDisplayName });
                AppendReportLine(doc, UiTextKey.DocumentationReportCallQueueResourceUpnLabel, "  CQ Resource UPN: {value}", new Dictionary<string, object?> { ["value"] = variables.RacqUPN });
                AppendReportLine(doc, UiTextKey.DocumentationReportAutoAttendantResourceUpnLabel, "  AA Resource UPN: {value}", new Dictionary<string, object?> { ["value"] = variables.RaaaUPN });
                AppendReportLine(doc, UiTextKey.DocumentationReportPhoneNumberLabel, "  Phone Number:    {value}", new Dictionary<string, object?> { ["value"] = variables.RaaAnr });
                doc.AppendLine();
            }

            doc.AppendLine("═══════════════════════════════════════════════════════════════");
            AppendReportLine(doc, UiTextKey.DocumentationReportEndTitle, "  END OF DOCUMENTATION");
            doc.AppendLine("═══════════════════════════════════════════════════════════════");
        }

        // ─────────────────────── TOPOLOGY BUILDER ───────────────────────

        private void BuildTopology(StringBuilder doc,
            List<RaInfo> raList, List<AaInfo> aaList, List<CqInfo> cqList,
            List<MenuOption> menuOptions, List<AssocInfo> assocList,
            List<AgentInfo> agentList,
            List<OverflowTimeout> overflowList, List<OverflowTimeout> timeoutList,
            List<(string AaName, string OpType, string OpId)> operatorList)
        {
            AppendReportLine(doc, UiTextKey.DocumentationReportTopologyIntro, "  This section shows the complete call routing chain from");
            AppendReportLine(doc, UiTextKey.DocumentationReportTopologyIntroContinuation, "  phone numbers through to final destinations.");
            doc.AppendLine();

            // Build lookup: RA → associated AA/CQ
            var assocByRaId = assocList.ToDictionary(a => a.RaId, a => a, StringComparer.OrdinalIgnoreCase);
            var raByObjId = raList.ToDictionary(r => r.ObjectId, r => r, StringComparer.OrdinalIgnoreCase);

            // Find RAs with phone numbers (entry points)
            var entryPoints = raList.Where(r => !string.IsNullOrEmpty(r.Phone)).OrderBy(r => r.Phone).ToList();

            if (entryPoints.Count == 0 && raList.Count > 0)
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoAssignedNumbersWarning, "  ⚠ No resource accounts have phone numbers assigned.");
                AppendReportLine(doc, UiTextKey.DocumentationReportShowAssociations, "  Showing all resource account associations instead:");
                doc.AppendLine();
                entryPoints = raList.OrderBy(r => r.Name).ToList();
            }

            foreach (var entry in entryPoints)
            {
                var phone = !string.IsNullOrEmpty(entry.Phone)
                    ? entry.Phone
                    : GetText(UiTextKey.DocumentationReportNoNumberValue, "(no number)");
                doc.AppendLine($"  ☎ {phone}");
                AppendReportLine(doc, UiTextKey.DocumentationReportResourceAccountNode, "  └─► Resource Account: {name}", new Dictionary<string, object?> { ["name"] = entry.Name });

                if (assocByRaId.TryGetValue(entry.ObjectId, out var assoc))
                {
                    if (assoc.ConfigType.Contains("AutoAttendant", StringComparison.OrdinalIgnoreCase))
                    {
                        var aa = aaList.FirstOrDefault(a => a.Identity.Contains(assoc.ConfigId, StringComparison.OrdinalIgnoreCase));
                        var aaName = aa?.Name ?? assoc.ConfigId;
                        AppendReportLine(doc, UiTextKey.DocumentationReportAutoAttendantNode, "      └─► Auto Attendant: {name}", new Dictionary<string, object?> { ["name"] = aaName });

                        // Show default menu routing
                        var opts = menuOptions.Where(m => m.AaName == aaName && m.FlowName == "DefaultCallFlow").ToList();
                        if (opts.Count > 0)
                        {
                            foreach (var opt in opts)
                            {
                                var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                                AppendReportLine(doc, UiTextKey.DocumentationReportKeyActionNode, "          ├─ Key {key} → {action}: {target}", new Dictionary<string, object?> { ["key"] = opt.Key, ["action"] = opt.Action, ["target"] = targetName });

                                // If target is a CQ, show agents
                                ShowCqAgentsIfMatch(doc, opt.TargetId, cqList, agentList, raByObjId, "              ");
                            }
                        }

                        // Show operator
                        var op = operatorList.FirstOrDefault(o => o.AaName == aaName);
                        if (!string.IsNullOrEmpty(op.AaName))
                        {
                            var opName = ResolveTargetName(op.OpId, raList, aaList, cqList);
                            AppendReportLine(doc, UiTextKey.DocumentationReportOperatorNode, "          └─ Operator → {name}", new Dictionary<string, object?> { ["name"] = opName });
                        }
                    }
                    else if (assoc.ConfigType.Contains("CallQueue", StringComparison.OrdinalIgnoreCase))
                    {
                        var cq = cqList.FirstOrDefault(c => c.Identity.Contains(assoc.ConfigId, StringComparison.OrdinalIgnoreCase));
                        var cqName = cq?.Name ?? assoc.ConfigId;
                        AppendReportLine(doc, UiTextKey.DocumentationReportCallQueueNode, "      └─► Call Queue: {name} [{routing}]", new Dictionary<string, object?> { ["name"] = cqName, ["routing"] = cq?.Routing ?? "?" });

                        var agents = agentList.Where(a => a.CqName == cqName).ToList();
                        if (agents.Count > 0)
                        {
                            AppendReportLine(doc, UiTextKey.DocumentationReportAgentsCount, "          Agents ({count}):", new Dictionary<string, object?> { ["count"] = agents.Count });
                            foreach (var agent in agents.Take(10))
                                doc.AppendLine($"            • {agent.ObjectId}");
                            if (agents.Count > 10)
                                AppendReportLine(doc, UiTextKey.DocumentationReportMoreAgents, "            ... and {count} more", new Dictionary<string, object?> { ["count"] = agents.Count - 10 });
                        }

                        // Overflow/Timeout
                        var of = overflowList.FirstOrDefault(o => o.CqName == cqName);
                        if (of != null)
                        {
                            var ofTarget = ResolveTargetName(of.TargetId, raList, aaList, cqList);
                            AppendReportLine(doc, UiTextKey.DocumentationReportOverflowNode, "          Overflow → {target}", new Dictionary<string, object?> { ["target"] = ofTarget });
                        }
                        var to = timeoutList.FirstOrDefault(t => t.CqName == cqName);
                        if (to != null)
                        {
                            var toTarget = ResolveTargetName(to.TargetId, raList, aaList, cqList);
                            AppendReportLine(doc, UiTextKey.DocumentationReportTimeoutNode, "          Timeout  → {target}", new Dictionary<string, object?> { ["target"] = toTarget });
                        }
                    }
                }
                else
                {
                    AppendReportLine(doc, UiTextKey.DocumentationReportNotAssociatedNode, "      └─ (not associated with any AA or CQ)");
                }
                doc.AppendLine();
            }

            if (entryPoints.Count == 0)
            {
                AppendReportLine(doc, UiTextKey.DocumentationReportNoTopology, "  (No routing topology available — no resource accounts found)");
                doc.AppendLine();
            }
        }

        private void ShowCqAgentsIfMatch(StringBuilder doc, string targetId, List<CqInfo> cqList, List<AgentInfo> agentList, Dictionary<string, RaInfo> raByObjId, string indent)
        {
            // Check if targetId is an RA associated with a CQ
            if (raByObjId.TryGetValue(targetId, out var ra))
            {
                // This is fine, but we'd need association data to resolve further
                return;
            }

            // Check if target matches a CQ identity
            var matchedCq = cqList.FirstOrDefault(c => c.Identity.Contains(targetId, StringComparison.OrdinalIgnoreCase));
            if (matchedCq != null)
            {
                var agents = agentList.Where(a => a.CqName == matchedCq.Name).ToList();
                if (agents.Count > 0)
                {
                    AppendReportLine(doc, UiTextKey.DocumentationReportAgentsCount, $"{indent}Agents ({{count}}):", new Dictionary<string, object?> { ["count"] = agents.Count });
                    foreach (var agent in agents.Take(5))
                        doc.AppendLine($"{indent}  • {agent.ObjectId}");
                    if (agents.Count > 5)
                        AppendReportLine(doc, UiTextKey.DocumentationReportMoreAgents, $"{indent}  ... and {{count}} more", new Dictionary<string, object?> { ["count"] = agents.Count - 5 });
                }
            }
        }

        private string ResolveTargetName(string targetId, List<RaInfo> raList, List<AaInfo> aaList, List<CqInfo> cqList)
        {
            if (string.IsNullOrEmpty(targetId))
                return GetText(UiTextKey.DocumentationReportNoneValue, "(none)");

            // Try RA first
            var ra = raList.FirstOrDefault(r => r.ObjectId.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            if (ra != null)
            {
                return GetText(
                    UiTextKey.DocumentationReportResolvedResourceAccount,
                    "{name} (Resource Account)",
                    new Dictionary<string, object?> { ["name"] = ra.Name });
            }

            // Try AA
            var aa = aaList.FirstOrDefault(a => a.Identity.Contains(targetId, StringComparison.OrdinalIgnoreCase));
            if (aa != null)
            {
                return GetText(
                    UiTextKey.DocumentationReportResolvedAutoAttendant,
                    "{name} (Auto Attendant)",
                    new Dictionary<string, object?> { ["name"] = aa.Name });
            }

            // Try CQ
            var cq = cqList.FirstOrDefault(c => c.Identity.Contains(targetId, StringComparison.OrdinalIgnoreCase));
            if (cq != null)
            {
                return GetText(
                    UiTextKey.DocumentationReportResolvedCallQueue,
                    "{name} (Call Queue)",
                    new Dictionary<string, object?> { ["name"] = cq.Name });
            }

            return targetId;
        }

        private void AppendReportLine(
            StringBuilder doc,
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => doc.AppendLine(GetText(key, fallback, parameters));
    }
}
