using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.ViewModels
{
    /// <summary>
    /// Read-only tenant dashboard (issue #64). Loads the telephony topology through the standard
    /// PowerShell execution pipeline (so the read is throttle-retried and audited as Kind=Read),
    /// assembles it via <see cref="ITenantTopologyAssembler"/>, caches it for the session via
    /// <see cref="ITenantTopologyCache"/>, and exposes search + relationship drill-down + orphans.
    /// This page never mutates tenant state.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        private readonly ITenantTopologyAssembler _assembler;
        private readonly ITenantTopologyCache _cache;

        [ObservableProperty]
        private ObservableCollection<TopologyAutoAttendant> _autoAttendants = new();

        [ObservableProperty]
        private ObservableCollection<TopologyCallQueue> _callQueues = new();

        [ObservableProperty]
        private ObservableCollection<TopologyResourceAccount> _resourceAccounts = new();

        [ObservableProperty]
        private ObservableCollection<TopologyGroup> _groups = new();

        [ObservableProperty]
        private ObservableCollection<OrphanFinding> _orphans = new();

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private TopologyAutoAttendant? _selectedAutoAttendant;

        [ObservableProperty]
        private TopologyCallQueue? _selectedCallQueue;

        [ObservableProperty]
        private ObservableCollection<TopologyResourceAccount> _selectedAutoAttendantResourceAccounts = new();

        [ObservableProperty]
        private ObservableCollection<TopologyCallQueue> _selectedAutoAttendantCallQueues = new();

        [ObservableProperty]
        private ObservableCollection<string> _selectedAutoAttendantHolidayIds = new();

        [ObservableProperty]
        private ObservableCollection<TopologyResourceAccount> _selectedCallQueueResourceAccounts = new();

        [ObservableProperty]
        private ObservableCollection<TopologyGroup> _selectedCallQueueGroups = new();

        [ObservableProperty]
        private ObservableCollection<string> _selectedCallQueueAgentIds = new();

        [ObservableProperty]
        private DateTimeOffset? _lastRefreshedUtc;

        public DashboardViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ITenantTopologyAssembler assembler,
            ITenantTopologyCache cache,
            ISharedStateService? sharedStateService = null,
            IDialogService? dialogService = null,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService,
                  sharedStateService, dialogService, auditLog, translationService)
        {
            _assembler = assembler;
            _cache = cache;
            _loggingService.Log("Dashboard page loaded", LogLevel.Info);

            // Instant restore from the session cache (issue #64: navigation back is instant).
            if (_cache.HasValue && _cache.Current is not null)
            {
                Populate(_cache.Current);
            }
        }

        // Filtered projections used by the UI. Recomputed whenever the source list or search changes.
        public ObservableCollection<TopologyAutoAttendant> AutoAttendantsView =>
            new(AutoAttendants.Where(MatchesAutoAttendant));

        public ObservableCollection<TopologyCallQueue> CallQueuesView =>
            new(CallQueues.Where(MatchesCallQueue));

        public ObservableCollection<TopologyResourceAccount> ResourceAccountsView =>
            new(ResourceAccounts.Where(MatchesResourceAccount));

        public ObservableCollection<TopologyGroup> GroupsView =>
            new(Groups.Where(MatchesGroup));

        public bool HasData => AutoAttendants.Count > 0 || CallQueues.Count > 0
            || ResourceAccounts.Count > 0 || Groups.Count > 0;

        public bool HasOrphans => Orphans.Count > 0;

        public int OrphanCount => Orphans.Count;

        public string LastRefreshedText => LastRefreshedUtc is { } ts
            ? $"Last refreshed {ts.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC"
            : "Not yet loaded";

        /// <summary>Loads from the session cache when present; otherwise performs the first query.</summary>
        [RelayCommand]
        private async Task LoadAsync()
        {
            if (_cache.HasValue && _cache.Current is not null)
            {
                Populate(_cache.Current);
                return;
            }

            await RefreshAsync();
        }

        /// <summary>Re-queries the tenant and refreshes the cache. Idempotent read (audited Kind=Read).</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            await RunBusyAsync(async () =>
            {
                StatusMessage = "Retrieving tenant topology...";
                var command = _powerShellCommandService.GetRetrieveTenantTopologyCommand();
                var result = await ExecutePowerShellCommandAsync(command, null, "RetrieveTenantTopology", allowThrottleRetry: true);

                var topology = _assembler.Assemble(result.Value, DateTimeOffset.UtcNow);
                _cache.Set(topology);
                Populate(topology);

                StatusMessage = $"Loaded {AutoAttendants.Count} auto attendants, {CallQueues.Count} call queues, "
                    + $"{ResourceAccounts.Count} resource accounts, {Groups.Count} groups. {OrphanCount} orphan(s).";
                _loggingService.Log(StatusMessage, LogLevel.Info);
            }, nameof(RefreshAsync), "Retrieving tenant topology...");
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        private void Populate(TenantTopology topology)
        {
            AutoAttendants = new ObservableCollection<TopologyAutoAttendant>(topology.AutoAttendants);
            CallQueues = new ObservableCollection<TopologyCallQueue>(topology.CallQueues);
            ResourceAccounts = new ObservableCollection<TopologyResourceAccount>(topology.ResourceAccounts);
            Groups = new ObservableCollection<TopologyGroup>(topology.Groups);
            Orphans = new ObservableCollection<OrphanFinding>(topology.Orphans);
            LastRefreshedUtc = topology.RetrievedAtUtc == DateTimeOffset.MinValue ? null : topology.RetrievedAtUtc;

            SelectedAutoAttendant = null;
            SelectedCallQueue = null;

            RaiseViewsChanged();
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(HasOrphans));
            OnPropertyChanged(nameof(OrphanCount));
        }

        private void RaiseViewsChanged()
        {
            OnPropertyChanged(nameof(AutoAttendantsView));
            OnPropertyChanged(nameof(CallQueuesView));
            OnPropertyChanged(nameof(ResourceAccountsView));
            OnPropertyChanged(nameof(GroupsView));
        }

        partial void OnSearchTextChanged(string value) => RaiseViewsChanged();

        partial void OnLastRefreshedUtcChanged(DateTimeOffset? value)
            => OnPropertyChanged(nameof(LastRefreshedText));

        partial void OnSelectedAutoAttendantChanged(TopologyAutoAttendant? value)
        {
            var ids = value?.ResourceAccountObjectIds ?? (IReadOnlyList<string>)Array.Empty<string>();
            var targetIds = value?.CallTargetObjectIds ?? (IReadOnlyList<string>)Array.Empty<string>();

            SelectedAutoAttendantResourceAccounts = new ObservableCollection<TopologyResourceAccount>(
                ResourceAccounts.Where(ra => ids.Contains(ra.ObjectId, StringComparer.OrdinalIgnoreCase)));

            SelectedAutoAttendantHolidayIds = new ObservableCollection<string>(
                value?.HolidayScheduleIds ?? Enumerable.Empty<string>());

            // AA -> CQ: a call queue is linked when one of its resource accounts is a call-flow target.
            SelectedAutoAttendantCallQueues = new ObservableCollection<TopologyCallQueue>(
                CallQueues.Where(cq => cq.ResourceAccountObjectIds
                    .Any(raId => targetIds.Contains(raId, StringComparer.OrdinalIgnoreCase))));
        }

        partial void OnSelectedCallQueueChanged(TopologyCallQueue? value)
        {
            var raIds = value?.ResourceAccountObjectIds ?? (IReadOnlyList<string>)Array.Empty<string>();
            var dlIds = value?.DistributionListIds ?? (IReadOnlyList<string>)Array.Empty<string>();

            SelectedCallQueueResourceAccounts = new ObservableCollection<TopologyResourceAccount>(
                ResourceAccounts.Where(ra => raIds.Contains(ra.ObjectId, StringComparer.OrdinalIgnoreCase)));

            SelectedCallQueueGroups = new ObservableCollection<TopologyGroup>(
                Groups.Where(g => dlIds.Contains(g.Id, StringComparer.OrdinalIgnoreCase)));

            SelectedCallQueueAgentIds = new ObservableCollection<string>(
                value?.AgentObjectIds ?? Enumerable.Empty<string>());
        }

        private bool MatchesAutoAttendant(TopologyAutoAttendant aa)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            var q = SearchText.Trim();
            return Contains(aa.Name, q) || Contains(aa.Identity, q)
                || Contains(aa.LanguageId, q) || Contains(aa.TimeZoneId, q);
        }

        private bool MatchesCallQueue(TopologyCallQueue cq)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            var q = SearchText.Trim();
            return Contains(cq.Name, q) || Contains(cq.Identity, q) || Contains(cq.RoutingMethod, q);
        }

        private bool MatchesResourceAccount(TopologyResourceAccount ra)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            var q = SearchText.Trim();
            return Contains(ra.DisplayName, q) || Contains(ra.UserPrincipalName, q)
                || Contains(ra.PhoneNumber, q) || Contains(ra.ObjectId, q);
        }

        private bool MatchesGroup(TopologyGroup g)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            var q = SearchText.Trim();
            return Contains(g.DisplayName, q) || Contains(g.MailNickname, q)
                || Contains(g.Description, q) || Contains(g.Id, q);
        }

        private static bool Contains(string? value, string query)
            => !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
