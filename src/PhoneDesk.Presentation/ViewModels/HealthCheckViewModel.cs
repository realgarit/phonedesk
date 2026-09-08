using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.HealthChecks;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.ViewModels;

public partial class HealthCheckViewModel : ViewModelBase, IDisposable
{
    private readonly ITenantTopologyCache _topologyCache;
    private readonly ITenantHealthEvaluationService _evaluationService;
    private readonly ITenantHealthEnrichmentParser _enrichmentParser;
    private readonly ITenantHealthQueryBuilder _queryBuilder;
    private readonly ITenantHealthPreferencesStore _preferencesStore;
    private readonly ITenantHealthCheckCache _healthCheckCache;
    private TenantHealthPreferences _preferences = TenantHealthPreferences.Default;
    private TenantHealthContext? _lastContext;
    private IReadOnlyList<TenantHealthFinding> _evaluatedFindings = Array.Empty<TenantHealthFinding>();
    private string? _preferencesTenantId;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<HealthRuleToggleItem> _rules = new();

    [ObservableProperty]
    private ObservableCollection<HealthFindingDisplayItem> _activeFindings = new();

    [ObservableProperty]
    private ObservableCollection<HealthFindingDisplayItem> _suppressedFindings = new();

    [ObservableProperty]
    private bool _hasRun;

    [ObservableProperty]
    private bool _showSuppressed;

    [ObservableProperty]
    private DateTimeOffset? _lastRunUtc;

    public bool HasActiveFindings => ActiveFindings.Count > 0;
    public bool HasSuppressedFindings => SuppressedFindings.Count > 0;
    public bool ShowHealthyState => HasRun && !HasActiveFindings;
    public bool ShowNoResultsState => !HasRun;

    public string LastRunText => LastRunUtc is { } value
        ? GetText(
            UiTextKey.HealthCheckLastRunLabel,
            "Last run: {time}",
            new Dictionary<string, object?> { ["time"] = value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") })
        : string.Empty;

    public HealthCheckViewModel(
        IPowerShellContextService powerShellContextService,
        IPowerShellCommandService powerShellCommandService,
        ILoggingService loggingService,
        ISessionManager sessionManager,
        INavigationService navigationService,
        IErrorHandlingService errorHandlingService,
        IValidationService validationService,
        ITenantTopologyCache topologyCache,
        ITenantHealthEvaluationService evaluationService,
        ITenantHealthEnrichmentParser enrichmentParser,
        ITenantHealthQueryBuilder queryBuilder,
        ITenantHealthPreferencesStore preferencesStore,
        ITenantHealthCheckCache healthCheckCache,
        ISharedStateService? sharedStateService = null,
        IDialogService? dialogService = null,
        IAuditLog? auditLog = null,
        ITranslationService? translationService = null)
        : base(
            powerShellContextService,
            powerShellCommandService,
            loggingService,
            sessionManager,
            navigationService,
            errorHandlingService,
            validationService,
            sharedStateService,
            dialogService,
            auditLog,
            translationService)
    {
        _topologyCache = topologyCache;
        _evaluationService = evaluationService;
        _enrichmentParser = enrichmentParser;
        _queryBuilder = queryBuilder;
        _preferencesStore = preferencesStore;
        _healthCheckCache = healthCheckCache;
        LoadPreferencesAndRules();
        if (_translationService is not null)
        {
            _translationService.PropertyChanged += OnTranslationServicePropertyChanged;
        }
        LogLocalized(
            UiTextKey.HealthCheckPageLoadedLog,
            "Tenant health-check page loaded",
            LogLevel.Info);
    }

    [RelayCommand]
    private async Task RunHealthCheckAsync()
    {
        var tenantId = _sessionManager.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            StatusMessage = GetText(
                UiTextKey.HealthCheckTenantRequiredStatus,
                "Reconnect to Teams and Microsoft Graph so the tenant can be identified.");
            return;
        }
        if (_topologyCache.Current is null ||
            !string.Equals(_topologyCache.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = GetText(
                UiTextKey.HealthCheckLoadedTopologyRequiredStatus,
                "Load the tenant topology on the Dashboard before running a health check.");
            return;
        }

        if (!string.Equals(_preferencesTenantId, tenantId, StringComparison.Ordinal))
        {
            LoadPreferencesAndRules();
        }

        try
        {
            IsBusy = true;
            StatusMessage = GetText(
                UiTextKey.HealthCheckRunningStatus,
                "Reading health-check data and evaluating enabled rules...");
            var command = _queryBuilder.GetTenantHealthEnrichmentCommand();
            var result = await ExecutePowerShellCommandAsync(
                command,
                environmentVariables: null,
                "TenantHealthCheck",
                allowThrottleRetry: true);
            if (!result.IsSuccess)
            {
                throw new InvalidDataException(result.ErrorMessage ?? GetText(
                    UiTextKey.HealthCheckQueryFailedDetail,
                    "The health-check query did not complete successfully."));
            }

            var enrichment = _enrichmentParser.Parse(result.Value ?? string.Empty);
            _lastContext = new TenantHealthContext(tenantId, _topologyCache.Current, enrichment);
            LastRunUtc = DateTimeOffset.UtcNow;
            HasRun = true;
            EvaluateAndPublish();
        }
        catch (Exception ex)
        {
            StatusMessage = GetText(
                UiTextKey.HealthCheckFailedStatus,
                "Health check failed: {error}",
                new Dictionary<string, object?> { ["error"] = ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDashboard() => NavigateTo(ConstantsService.Pages.Dashboard);

    [RelayCommand]
    private void OpenFinding(HealthFindingDisplayItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Finding.DestinationPage))
        {
            NavigateTo(item.Finding.DestinationPage);
        }
    }

    [RelayCommand]
    private void SuppressFinding(HealthFindingDisplayItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(_preferencesTenantId))
        {
            return;
        }
        var suppressed = _preferences.SuppressedFindingIds.ToHashSet(StringComparer.Ordinal);
        suppressed.Add(item.Finding.FindingId);
        SavePreferences(_preferences with { SuppressedFindingIds = suppressed });
    }

    [RelayCommand]
    private void RestoreFinding(HealthFindingDisplayItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(_preferencesTenantId))
        {
            return;
        }
        var suppressed = _preferences.SuppressedFindingIds.ToHashSet(StringComparer.Ordinal);
        suppressed.Remove(item.Finding.FindingId);
        SavePreferences(_preferences with { SuppressedFindingIds = suppressed });
    }

    private void LoadPreferencesAndRules()
    {
        _preferencesTenantId = _sessionManager.TenantId;
        _preferences = string.IsNullOrWhiteSpace(_preferencesTenantId)
            ? TenantHealthPreferences.Default
            : _preferencesStore.Load(_preferencesTenantId);
        BuildRules();
    }

    private void BuildRules()
    {
        Rules = new ObservableCollection<HealthRuleToggleItem>(TenantHealthRuleCatalog.Rules.Select(rule =>
            new HealthRuleToggleItem(
                rule.Id,
                Localize(rule.Title),
                !_preferences.DisabledRuleIds.Contains(rule.Id),
                OnRuleToggled)));
    }

    public void SetResultsForDisplay(
        TenantHealthContext context,
        TenantHealthPreferences preferences,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preferences);
        _lastContext = context;
        _preferencesTenantId = context.TenantId;
        _preferences = preferences;
        BuildRules();
        LastRunUtc = evaluatedAtUtc;
        HasRun = true;
        EvaluateAndPublish();
    }

    private void OnRuleToggled(HealthRuleToggleItem item)
    {
        if (string.IsNullOrWhiteSpace(_preferencesTenantId))
        {
            return;
        }
        var disabled = _preferences.DisabledRuleIds.ToHashSet(StringComparer.Ordinal);
        if (item.IsEnabled)
        {
            disabled.Remove(item.RuleId);
        }
        else
        {
            disabled.Add(item.RuleId);
        }
        SavePreferences(_preferences with { DisabledRuleIds = disabled });
    }

    private void SavePreferences(TenantHealthPreferences updated)
    {
        if (string.IsNullOrWhiteSpace(_preferencesTenantId) ||
            !_preferencesStore.Save(_preferencesTenantId, updated))
        {
            StatusMessage = GetText(
                UiTextKey.HealthCheckPreferencesSaveFailedStatus,
                "Health-check preferences could not be saved. The existing settings file was left unchanged.");
            LoadPreferencesAndRules();
            return;
        }

        _preferences = updated;
        if (_lastContext is not null)
        {
            EvaluateAndPublish();
        }
    }

    private void EvaluateAndPublish()
    {
        if (_lastContext is null || string.IsNullOrWhiteSpace(_preferencesTenantId))
        {
            return;
        }
        var enabledRuleIds = Rules
            .Where(item => item.IsEnabled)
            .Select(item => item.RuleId)
            .ToHashSet(StringComparer.Ordinal);
        _evaluatedFindings = _evaluationService.Evaluate(_lastContext, enabledRuleIds);
        RefreshFindingDisplays();

        _healthCheckCache.Set(new TenantHealthCheckResult(
            _lastContext.TenantId,
            LastRunUtc ?? DateTimeOffset.UtcNow,
            _evaluatedFindings
                .Where(finding => !_preferences.SuppressedFindingIds.Contains(finding.FindingId))
                .ToArray(),
            _evaluatedFindings.Count(finding => _preferences.SuppressedFindingIds.Contains(finding.FindingId)),
            TenantHealthRuleCatalog.Rules.Count - enabledRuleIds.Count));
        StatusMessage = GetText(
            UiTextKey.HealthCheckCompletedStatus,
            "Health check completed: {count} active finding(s), {suppressed} suppressed.",
            new Dictionary<string, object?>
            {
                ["count"] = ActiveFindings.Count,
                ["suppressed"] = SuppressedFindings.Count,
            });
    }

    private void RefreshFindingDisplays()
    {
        ActiveFindings = new ObservableCollection<HealthFindingDisplayItem>(
            _evaluatedFindings
                .Where(finding => !_preferences.SuppressedFindingIds.Contains(finding.FindingId))
                .Select(CreateDisplay));
        SuppressedFindings = new ObservableCollection<HealthFindingDisplayItem>(
            _evaluatedFindings
                .Where(finding => _preferences.SuppressedFindingIds.Contains(finding.FindingId))
                .Select(CreateDisplay));
        if (SuppressedFindings.Count == 0)
        {
            ShowSuppressed = false;
        }
        OnPropertyChanged(nameof(HasActiveFindings));
        OnPropertyChanged(nameof(HasSuppressedFindings));
        OnPropertyChanged(nameof(ShowHealthyState));
        OnPropertyChanged(nameof(ShowNoResultsState));
    }

    private HealthFindingDisplayItem CreateDisplay(TenantHealthFinding finding)
    {
        var ruleTitle = TenantHealthRuleCatalog.Rules.First(rule => rule.Id == finding.RuleId).Title;
        var severity = finding.Severity switch
        {
            TenantHealthSeverity.Error => GetText(UiTextKey.HealthCheckSeverityError, "Error"),
            TenantHealthSeverity.Warning => GetText(UiTextKey.HealthCheckSeverityWarning, "Warning"),
            _ => GetText(UiTextKey.HealthCheckSeverityInformation, "Information"),
        };
        return new HealthFindingDisplayItem(
            finding,
            Localize(ruleTitle),
            severity,
            Localize(finding.Explanation),
            Localize(finding.Recommendation));
    }

    private string Localize(LocalizedHealthText text)
        => _translationService?.CurrentLanguage == AppLanguage.German ? text.German : text.English;

    partial void OnHasRunChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHealthyState));
        OnPropertyChanged(nameof(ShowNoResultsState));
    }

    partial void OnLastRunUtcChanged(DateTimeOffset? value)
        => OnPropertyChanged(nameof(LastRunText));

    private void OnTranslationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ITranslationService.CurrentLanguage))
        {
            return;
        }
        foreach (var rule in Rules)
        {
            rule.Title = Localize(TenantHealthRuleCatalog.Rules.First(item => item.Id == rule.RuleId).Title);
        }
        if (_lastContext is not null)
        {
            EvaluateAndPublish();
        }
        else
        {
            RefreshFindingDisplays();
        }
        OnPropertyChanged(nameof(LastRunText));
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
}

public partial class HealthRuleToggleItem : ObservableObject
{
    private readonly Action<HealthRuleToggleItem> _onChanged;

    public string RuleId { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isEnabled;

    public HealthRuleToggleItem(string ruleId, string title, bool isEnabled, Action<HealthRuleToggleItem> onChanged)
    {
        RuleId = ruleId;
        _title = title;
        _isEnabled = isEnabled;
        _onChanged = onChanged;
    }

    partial void OnIsEnabledChanged(bool value) => _onChanged(this);
}

public sealed record HealthFindingDisplayItem(
    TenantHealthFinding Finding,
    string RuleTitle,
    string SeverityLabel,
    string Explanation,
    string Recommendation)
{
    public bool CanOpen => !string.IsNullOrWhiteSpace(Finding.DestinationPage);
    public bool IsError => Finding.Severity == TenantHealthSeverity.Error;
    public bool IsWarning => Finding.Severity == TenantHealthSeverity.Warning;
    public bool IsInformation => Finding.Severity == TenantHealthSeverity.Information;
}
