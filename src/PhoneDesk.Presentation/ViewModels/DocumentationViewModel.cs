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

namespace PhoneDesk.ViewModels
{
    public partial class DocumentationViewModel : ViewModelBase
    {
        private readonly IDocumentationScriptBuilder _docBuilder;

        [ObservableProperty]
        private string _documentationOutput = string.Empty;

        [ObservableProperty]
        private bool _isExporting = false;

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
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _docBuilder = docBuilder;
            _loggingService.Log("Documentation page loaded", LogLevel.Info);
        }

        [RelayCommand]
        private async Task ExportDocumentationAsync()
        {
            try
            {
                IsBusy = true;
                IsExporting = true;
                StatusMessage = "Gathering tenant documentation...";

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
                StatusMessage = "Exporting tenant info... (1/7)";
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
                StatusMessage = "Exporting resource accounts... (2/7)";
                var raResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportResourceAccountsCommand(), null, "ExportResourceAccounts", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(raResult.Value))
                    ParseResourceAccountData(raResult.Value, raList, assocList);

                // 3. Auto Attendants
                StatusMessage = "Exporting auto attendants... (3/7)";
                var aaResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportAutoAttendantsCommand(), null, "ExportAutoAttendants", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(aaResult.Value))
                    ParseAutoAttendantData(aaResult.Value, aaList, menuOptions, callFlows, chaList, operatorList);

                // 4. Call Queues
                StatusMessage = "Exporting call queues... (4/7)";
                var cqResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportCallQueuesCommand(), null, "ExportCallQueues", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(cqResult.Value))
                    ParseCallQueueData(cqResult.Value, cqList, agentList, overflowList, timeoutList, dlList);

                // 5. Schedules
                StatusMessage = "Exporting schedules... (5/7)";
                var schedResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportSchedulesCommand(), null, "ExportSchedules", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(schedResult.Value))
                    ParseScheduleData(schedResult.Value, schedList, schedDateRanges, schedWeekly);

                // 6. Phone Numbers
                StatusMessage = "Exporting phone numbers... (6/7)";
                var phoneResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportPhoneNumbersCommand(), null, "ExportPhoneNumbers", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(phoneResult.Value))
                    ParsePhoneData(phoneResult.Value, phoneList);

                // 7. Voice Users
                StatusMessage = "Exporting voice users... (7/7)";
                var userResult = await ExecutePowerShellCommandAsync(_docBuilder.GetExportVoiceUsersCommand(), null, "ExportVoiceUsers", allowThrottleRetry: true);
                if (!string.IsNullOrEmpty(userResult.Value))
                    ParseUserData(userResult.Value, userList);

                // Build the comprehensive documentation
                StatusMessage = "Building documentation...";
                var doc = new StringBuilder();
                BuildDocumentation(doc, tenantName, tenantId, tenantCountry, tenantLang,
                    raList, aaList, cqList, menuOptions, callFlows, chaList, assocList,
                    agentList, phoneList, userList, schedList, schedDateRanges, schedWeekly,
                    overflowList, timeoutList, operatorList, dlList);

                DocumentationOutput = doc.ToString();
                StatusMessage = "Documentation exported successfully. You can copy the text above.";
                _loggingService.Log("Documentation exported successfully", LogLevel.Info);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _loggingService.Log($"Exception in ExportDocumentationAsync: {ex}", LogLevel.Error);
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
                StatusMessage = "No documentation to copy. Export first.";
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
                        StatusMessage = "Documentation copied to clipboard!";
                        _loggingService.Log("Documentation copied to clipboard", LogLevel.Info);
                        return;
                    }
                }
                StatusMessage = "Clipboard not available.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to copy: {ex.Message}";
                _loggingService.Log($"Failed to copy documentation to clipboard: {ex.Message}", LogLevel.Error);
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
            doc.AppendLine("  TEAMS PHONE SYSTEM — COMPLETE TENANT DOCUMENTATION");
            doc.AppendLine("═══════════════════════════════════════════════════════════════");
            doc.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            doc.AppendLine();

            // ──── 1. TENANT INFO ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  1. TENANT INFORMATION                                      │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (!string.IsNullOrEmpty(tenantName))
            {
                doc.AppendLine($"  Tenant Name:    {tenantName}");
                doc.AppendLine($"  Tenant ID:      {tenantId}");
                doc.AppendLine($"  Country:        {tenantCountry}");
                doc.AppendLine($"  Language:       {tenantLang}");
            }
            else
            {
                doc.AppendLine("  (Tenant information not available)");
            }
            doc.AppendLine();

            // ──── 2. EXECUTIVE SUMMARY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  2. EXECUTIVE SUMMARY                                       │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            doc.AppendLine($"  Auto Attendants:    {aaList.Count}");
            doc.AppendLine($"  Call Queues:        {cqList.Count}");
            doc.AppendLine($"  Resource Accounts:  {raList.Count}");
            doc.AppendLine($"  Phone Numbers:      {phoneList.Count}");
            doc.AppendLine($"  Voice Users:        {userList.Count}");
            doc.AppendLine($"  Schedules:          {schedList.Count}");
            var assignedNumbers = phoneList.Count(p => !string.IsNullOrEmpty(p.Status) && p.Status.Contains("Assign", StringComparison.OrdinalIgnoreCase));
            var unassigned = phoneList.Count - assignedNumbers;
            doc.AppendLine($"  Numbers Assigned:   {assignedNumbers}");
            doc.AppendLine($"  Numbers Unassigned: {unassigned}");
            doc.AppendLine();

            // ──── 3. CALL ROUTING TOPOLOGY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  3. CALL ROUTING TOPOLOGY                                   │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            BuildTopology(doc, raList, aaList, cqList, menuOptions, assocList, agentList, overflowList, timeoutList, operatorList);

            // ──── 4. PHONE NUMBER INVENTORY ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  4. PHONE NUMBER INVENTORY                                  │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (phoneList.Count > 0)
            {
                // Build RA lookup by ObjectId for resolving names
                var raById = raList.ToDictionary(r => r.ObjectId, r => r, StringComparer.OrdinalIgnoreCase);
                var userByUpn = userList.ToDictionary(u => u.Upn, u => u, StringComparer.OrdinalIgnoreCase);

                doc.AppendLine("  Number               │ Type     │ Status     │ Assigned To               │ City");
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
                doc.AppendLine("  (No phone numbers found or insufficient permissions)");
            }
            doc.AppendLine();

            // ──── 5. RESOURCE ACCOUNTS ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  5. RESOURCE ACCOUNTS                                       │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (raList.Count > 0)
            {
                foreach (var ra in raList.OrderBy(r => r.Name))
                {
                    var appType = ra.AppId switch
                    {
                        "11cd3e2e-fccb-42ad-ad00-878b93575e07" => "Call Queue",
                        "ce933385-9390-45d1-9512-c8d228074e07" => "Auto Attendant",
                        _ => ra.AppId
                    };
                    var assoc = assocList.FirstOrDefault(a => a.RaId.Equals(ra.ObjectId, StringComparison.OrdinalIgnoreCase));
                    var assocTarget = assoc != null ? $"→ {assoc.ConfigType}: {assoc.ConfigId}" : "(not associated)";

                    doc.AppendLine($"  ● {ra.Name}");
                    doc.AppendLine($"    UPN:         {ra.Upn}");
                    doc.AppendLine($"    Type:        {appType}");
                    doc.AppendLine($"    Phone:       {(string.IsNullOrEmpty(ra.Phone) ? "(none)" : ra.Phone)}");
                    doc.AppendLine($"    Association: {assocTarget}");
                    doc.AppendLine();
                }
            }
            else
            {
                doc.AppendLine("  (No resource accounts found)");
                doc.AppendLine();
            }

            // ──── 6. AUTO ATTENDANTS (DETAILED) ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  6. AUTO ATTENDANTS — DETAILED CONFIGURATION                │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (aaList.Count > 0)
            {
                foreach (var aa in aaList.OrderBy(a => a.Name))
                {
                    doc.AppendLine($"  ╔═ {aa.Name} ═══════════════════════════════════════");
                    doc.AppendLine($"  ║  Language:  {aa.Language}");
                    doc.AppendLine($"  ║  TimeZone:  {aa.TimeZone}");
                    doc.AppendLine($"  ║  Voice:     {aa.Voice}");
                    doc.AppendLine($"  ║  Default:   {aa.DefaultFlow}");

                    // Operator
                    var op = operatorList.FirstOrDefault(o => o.AaName == aa.Name);
                    if (!string.IsNullOrEmpty(op.AaName))
                    {
                        var opName = ResolveTargetName(op.OpId, raList, aaList, cqList);
                        doc.AppendLine($"  ║  Operator:  {opName} ({op.OpType})");
                    }

                    // Call Flows
                    var flows = callFlows.Where(f => f.AaName == aa.Name).ToList();
                    if (flows.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        doc.AppendLine($"  ║  Call Flows:");
                        foreach (var cf in flows)
                        {
                            doc.AppendLine($"  ║    ├─ {cf.FlowName} (Menu: {cf.MenuName})");
                            var opts = menuOptions.Where(m => m.AaName == aa.Name && m.FlowName == cf.FlowName).ToList();
                            foreach (var opt in opts)
                            {
                                var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                                doc.AppendLine($"  ║    │    Key {opt.Key} → {opt.Action}: {targetName}");
                            }
                        }
                    }

                    // Default call flow menu options
                    var defaultOpts = menuOptions.Where(m => m.AaName == aa.Name && m.FlowName == "DefaultCallFlow").ToList();
                    if (defaultOpts.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        doc.AppendLine($"  ║  Default Menu Options:");
                        foreach (var opt in defaultOpts)
                        {
                            var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                            doc.AppendLine($"  ║    Key {opt.Key} → {opt.Action}: {targetName}");
                        }
                    }

                    // Schedule associations
                    var chas = chaList.Where(c => c.AaName == aa.Name).ToList();
                    if (chas.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        doc.AppendLine($"  ║  Schedule Associations:");
                        foreach (var cha in chas)
                        {
                            doc.AppendLine($"  ║    {cha.Type}: Schedule={cha.ScheduleId}, CallFlow={cha.CallFlowId}");
                        }
                    }

                    doc.AppendLine($"  ╚═══════════════════════════════════════════════════════");
                    doc.AppendLine();
                }
            }
            else
            {
                doc.AppendLine("  (No auto attendants found)");
                doc.AppendLine();
            }

            // ──── 7. CALL QUEUES (DETAILED) ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  7. CALL QUEUES — DETAILED CONFIGURATION                    │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (cqList.Count > 0)
            {
                foreach (var cq in cqList.OrderBy(c => c.Name))
                {
                    doc.AppendLine($"  ╔═ {cq.Name} ═══════════════════════════════════════");
                    doc.AppendLine($"  ║  Routing:      {cq.Routing}");
                    doc.AppendLine($"  ║  Alert Time:   {cq.AlertTime}s");
                    doc.AppendLine($"  ║  Language:     {cq.Language}");
                    doc.AppendLine($"  ║  Agents:       {cq.AgentCount}");

                    // Agent IDs
                    var agents = agentList.Where(a => a.CqName == cq.Name).ToList();
                    if (agents.Count > 0)
                    {
                        doc.AppendLine($"  ║");
                        doc.AppendLine($"  ║  Agent List (✓ = opted in, ✗ = opted out):");
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
                        doc.AppendLine($"  ║  Distribution Lists:");
                        foreach (var dl in dls)
                            doc.AppendLine($"  ║    • {dl.DlId}");
                    }

                    // Overflow
                    var of = overflowList.FirstOrDefault(o => o.CqName == cq.Name);
                    if (of != null)
                    {
                        var targetName = ResolveTargetName(of.TargetId, raList, aaList, cqList);
                        doc.AppendLine($"  ║");
                        doc.AppendLine($"  ║  Overflow:     {of.Action} (threshold >{of.Threshold}) → {targetName}");
                    }
                    else
                    {
                        doc.AppendLine($"  ║  Overflow:     {cq.OverflowAction} (threshold >{cq.OverflowThreshold})");
                    }

                    // Timeout
                    var to = timeoutList.FirstOrDefault(t => t.CqName == cq.Name);
                    if (to != null)
                    {
                        var targetName = ResolveTargetName(to.TargetId, raList, aaList, cqList);
                        doc.AppendLine($"  ║  Timeout:      {to.Action} (threshold >{to.Threshold}s) → {targetName}");
                    }
                    else
                    {
                        doc.AppendLine($"  ║  Timeout:      {cq.TimeoutAction} (threshold >{cq.TimeoutThreshold}s)");
                    }

                    doc.AppendLine($"  ╚═══════════════════════════════════════════════════════");
                    doc.AppendLine();
                }
            }
            else
            {
                doc.AppendLine("  (No call queues found)");
                doc.AppendLine();
            }

            // ──── 8. SCHEDULES ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  8. SCHEDULES (HOLIDAYS & BUSINESS HOURS)                   │");
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
                        doc.AppendLine($"    Holiday Dates:");
                        foreach (var r in ranges)
                            doc.AppendLine($"      {r.Start}  →  {r.End}");
                    }

                    // Weekly schedule
                    var weekly = schedWeekly.Where(w => w.ScheduleName == sched.Name).ToList();
                    if (weekly.Count > 0)
                    {
                        doc.AppendLine($"    Weekly Hours:");
                        foreach (var w in weekly)
                            doc.AppendLine($"      {w.Day,-10}  {w.Start} - {w.End}");
                    }
                    doc.AppendLine();
                }
            }
            else
            {
                doc.AppendLine("  (No schedules found)");
                doc.AppendLine();
            }

            // ──── 9. VOICE-ENABLED USERS ────
            doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
            doc.AppendLine("│  9. VOICE-ENABLED USERS                                     │");
            doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
            doc.AppendLine();
            if (userList.Count > 0)
            {
                doc.AppendLine("  Name                        │ UPN                          │ Phone          │ VRP        │ Calling Policy");
                doc.AppendLine("  ────────────────────────────┼──────────────────────────────┼────────────────┼────────────┼──────────────");
                foreach (var u in userList.OrderBy(x => x.Name))
                {
                    doc.AppendLine($"  {u.Name,-28} │ {u.Upn,-28} │ {u.LineUri,-14} │ {u.VoiceRoutingPolicy,-10} │ {u.CallingPolicy}");
                }
            }
            else
            {
                doc.AppendLine("  (No voice-enabled users found or insufficient permissions)");
            }
            doc.AppendLine();

            // ──── 10. CURRENT CONFIGURATION ────
            var variables = _sharedStateService?.Variables;
            if (variables != null && !string.IsNullOrEmpty(variables.Customer))
            {
                doc.AppendLine("┌─────────────────────────────────────────────────────────────┐");
                doc.AppendLine("│  10. CURRENT PHONE MANAGER CONFIGURATION                    │");
                doc.AppendLine("└─────────────────────────────────────────────────────────────┘");
                doc.AppendLine();
                doc.AppendLine($"  Customer:        {variables.Customer}");
                doc.AppendLine($"  Customer Group:  {variables.CustomerGroupName}");
                doc.AppendLine($"  Language:        {variables.LanguageId}");
                doc.AppendLine($"  Time Zone:       {variables.TimeZoneId}");
                doc.AppendLine($"  Usage Location:  {variables.UsageLocation}");
                doc.AppendLine($"  M365 Group:      {variables.M365Group}");
                doc.AppendLine($"  CQ Display Name: {variables.CqDisplayName}");
                doc.AppendLine($"  AA Display Name: {variables.AaDisplayName}");
                doc.AppendLine($"  CQ Resource UPN: {variables.RacqUPN}");
                doc.AppendLine($"  AA Resource UPN: {variables.RaaaUPN}");
                doc.AppendLine($"  Phone Number:    {variables.RaaAnr}");
                doc.AppendLine();
            }

            doc.AppendLine("═══════════════════════════════════════════════════════════════");
            doc.AppendLine("  END OF DOCUMENTATION");
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
            doc.AppendLine("  This section shows the complete call routing chain from");
            doc.AppendLine("  phone numbers through to final destinations.");
            doc.AppendLine();

            // Build lookup: RA → associated AA/CQ
            var assocByRaId = assocList.ToDictionary(a => a.RaId, a => a, StringComparer.OrdinalIgnoreCase);
            var raByObjId = raList.ToDictionary(r => r.ObjectId, r => r, StringComparer.OrdinalIgnoreCase);

            // Find RAs with phone numbers (entry points)
            var entryPoints = raList.Where(r => !string.IsNullOrEmpty(r.Phone)).OrderBy(r => r.Phone).ToList();

            if (entryPoints.Count == 0 && raList.Count > 0)
            {
                doc.AppendLine("  ⚠ No resource accounts have phone numbers assigned.");
                doc.AppendLine("  Showing all resource account associations instead:");
                doc.AppendLine();
                entryPoints = raList.OrderBy(r => r.Name).ToList();
            }

            foreach (var entry in entryPoints)
            {
                var phone = !string.IsNullOrEmpty(entry.Phone) ? entry.Phone : "(no number)";
                doc.AppendLine($"  ☎ {phone}");
                doc.AppendLine($"  └─► Resource Account: {entry.Name}");

                if (assocByRaId.TryGetValue(entry.ObjectId, out var assoc))
                {
                    if (assoc.ConfigType.Contains("AutoAttendant", StringComparison.OrdinalIgnoreCase))
                    {
                        var aa = aaList.FirstOrDefault(a => a.Identity.Contains(assoc.ConfigId, StringComparison.OrdinalIgnoreCase));
                        var aaName = aa?.Name ?? assoc.ConfigId;
                        doc.AppendLine($"      └─► Auto Attendant: {aaName}");

                        // Show default menu routing
                        var opts = menuOptions.Where(m => m.AaName == aaName && m.FlowName == "DefaultCallFlow").ToList();
                        if (opts.Count > 0)
                        {
                            foreach (var opt in opts)
                            {
                                var targetName = ResolveTargetName(opt.TargetId, raList, aaList, cqList);
                                doc.AppendLine($"          ├─ Key {opt.Key} → {opt.Action}: {targetName}");

                                // If target is a CQ, show agents
                                ShowCqAgentsIfMatch(doc, opt.TargetId, cqList, agentList, raByObjId, "              ");
                            }
                        }

                        // Show operator
                        var op = operatorList.FirstOrDefault(o => o.AaName == aaName);
                        if (!string.IsNullOrEmpty(op.AaName))
                        {
                            var opName = ResolveTargetName(op.OpId, raList, aaList, cqList);
                            doc.AppendLine($"          └─ Operator → {opName}");
                        }
                    }
                    else if (assoc.ConfigType.Contains("CallQueue", StringComparison.OrdinalIgnoreCase))
                    {
                        var cq = cqList.FirstOrDefault(c => c.Identity.Contains(assoc.ConfigId, StringComparison.OrdinalIgnoreCase));
                        var cqName = cq?.Name ?? assoc.ConfigId;
                        doc.AppendLine($"      └─► Call Queue: {cqName} [{cq?.Routing ?? "?"}]");

                        var agents = agentList.Where(a => a.CqName == cqName).ToList();
                        if (agents.Count > 0)
                        {
                            doc.AppendLine($"          Agents ({agents.Count}):");
                            foreach (var agent in agents.Take(10))
                                doc.AppendLine($"            • {agent.ObjectId}");
                            if (agents.Count > 10)
                                doc.AppendLine($"            ... and {agents.Count - 10} more");
                        }

                        // Overflow/Timeout
                        var of = overflowList.FirstOrDefault(o => o.CqName == cqName);
                        if (of != null)
                        {
                            var ofTarget = ResolveTargetName(of.TargetId, raList, aaList, cqList);
                            doc.AppendLine($"          Overflow → {ofTarget}");
                        }
                        var to = timeoutList.FirstOrDefault(t => t.CqName == cqName);
                        if (to != null)
                        {
                            var toTarget = ResolveTargetName(to.TargetId, raList, aaList, cqList);
                            doc.AppendLine($"          Timeout  → {toTarget}");
                        }
                    }
                }
                else
                {
                    doc.AppendLine($"      └─ (not associated with any AA or CQ)");
                }
                doc.AppendLine();
            }

            if (entryPoints.Count == 0)
            {
                doc.AppendLine("  (No routing topology available — no resource accounts found)");
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
                    doc.AppendLine($"{indent}Agents ({agents.Count}):");
                    foreach (var agent in agents.Take(5))
                        doc.AppendLine($"{indent}  • {agent.ObjectId}");
                    if (agents.Count > 5)
                        doc.AppendLine($"{indent}  ... and {agents.Count - 5} more");
                }
            }
        }

        private string ResolveTargetName(string targetId, List<RaInfo> raList, List<AaInfo> aaList, List<CqInfo> cqList)
        {
            if (string.IsNullOrEmpty(targetId)) return "(none)";

            // Try RA first
            var ra = raList.FirstOrDefault(r => r.ObjectId.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            if (ra != null) return $"{ra.Name} (RA)";

            // Try AA
            var aa = aaList.FirstOrDefault(a => a.Identity.Contains(targetId, StringComparison.OrdinalIgnoreCase));
            if (aa != null) return $"{aa.Name} (AA)";

            // Try CQ
            var cq = cqList.FirstOrDefault(c => c.Identity.Contains(targetId, StringComparison.OrdinalIgnoreCase));
            if (cq != null) return $"{cq.Name} (CQ)";

            return targetId;
        }
    }
}
