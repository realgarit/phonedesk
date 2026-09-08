using PhoneDesk.Services;
using PhoneDesk.Topology;

namespace PhoneDesk.HealthChecks;

public static class TenantHealthRuleCatalog
{
    public const string OrphanedResourceAccount = "orphaned-resource-account";
    public const string ResourceAccountLicense = "resource-account-license";
    public const string CallQueueWithoutAgents = "call-queue-without-agents";
    public const string AutoAttendantWithoutAfterHours = "auto-attendant-without-after-hours";
    public const string UnassignedServiceNumber = "unassigned-service-number";
    public const string ResourceAccountUsageLocation = "resource-account-usage-location";

    public static IReadOnlyList<TenantHealthRuleDefinition> Rules { get; } =
    [
        new(
            OrphanedResourceAccount,
            true,
            Text("Unassociated resource accounts", "Nicht zugeordnete Ressourcenkonten"),
            EvaluateOrphanedResourceAccounts),
        new(
            ResourceAccountLicense,
            true,
            Text("Resource-account licenses", "Lizenzen für Ressourcenkonten"),
            EvaluateResourceAccountLicenses),
        new(
            CallQueueWithoutAgents,
            true,
            Text("Call queues without agents", "Anrufwarteschleifen ohne Agenten"),
            EvaluateCallQueuesWithoutAgents),
        new(
            AutoAttendantWithoutAfterHours,
            true,
            Text("Auto attendants without after-hours handling", "Automatische Telefonzentralen ohne Behandlung ausserhalb der Geschäftszeiten"),
            EvaluateAutoAttendantsWithoutAfterHours),
        new(
            UnassignedServiceNumber,
            true,
            Text("Unassigned service numbers", "Nicht zugewiesene Dienstrufnummern"),
            EvaluateUnassignedServiceNumbers),
        new(
            ResourceAccountUsageLocation,
            true,
            Text("Resource-account usage locations", "Verwendungsstandorte für Ressourcenkonten"),
            EvaluateResourceAccountUsageLocations),
    ];

    private static IEnumerable<TenantHealthFinding> EvaluateOrphanedResourceAccounts(TenantHealthContext context)
    {
        var referenced = ReferencedResourceAccountIds(context.Topology);
        foreach (var account in context.Topology.ResourceAccounts.Where(account =>
                     string.IsNullOrWhiteSpace(account.ObjectId) || !referenced.Contains(account.ObjectId)))
        {
            yield return Finding(
                OrphanedResourceAccount,
                "unassociated",
                TenantHealthSeverity.Warning,
                "resourceAccount",
                account.ObjectId,
                Display(account.DisplayName, account.UserPrincipalName),
                Text(
                    "This resource account is not linked to an auto attendant or call queue.",
                    "Dieses Ressourcenkonto ist keiner automatischen Telefonzentrale oder Anrufwarteschleife zugeordnet."),
                Text(
                    "Associate the account with its intended voice application, or remove it if it is no longer needed.",
                    "Ordnen Sie das Konto der vorgesehenen Sprachanwendung zu oder entfernen Sie es, wenn es nicht mehr benötigt wird."),
                Destination(account.Kind));
        }
    }

    private static IEnumerable<TenantHealthFinding> EvaluateResourceAccountLicenses(TenantHealthContext context)
    {
        var states = context.Enrichment.ResourceAccounts.ToDictionary(item => item.ObjectId, StringComparer.OrdinalIgnoreCase);
        var referenced = ReferencedResourceAccountIds(context.Topology);
        foreach (var account in context.Topology.ResourceAccounts)
        {
            var state = states[account.ObjectId];
            if (!state.HasResourceAccountLicense)
            {
                yield return Finding(
                    ResourceAccountLicense,
                    "missing",
                    TenantHealthSeverity.Error,
                    "resourceAccount",
                    account.ObjectId,
                    Display(account.DisplayName, account.UserPrincipalName),
                    Text(
                        "The required Microsoft Teams Phone Resource Account license is not assigned.",
                        "Die erforderliche Microsoft Teams Phone-Ressourcenkontolizenz ist nicht zugewiesen."),
                    Text(
                        "Assign a Microsoft Teams Phone Resource Account license. Microsoft requires it even when the account has no phone number.",
                        "Weisen Sie eine Microsoft Teams Phone-Ressourcenkontolizenz zu. Microsoft verlangt sie auch ohne zugewiesene Rufnummer."),
                    Destination(account.Kind));
            }
            else if (!referenced.Contains(account.ObjectId))
            {
                yield return Finding(
                    ResourceAccountLicense,
                    "licensed-unassociated",
                    TenantHealthSeverity.Warning,
                    "resourceAccount",
                    account.ObjectId,
                    Display(account.DisplayName, account.UserPrincipalName),
                    Text(
                        "A resource-account license is assigned, but the account is not linked to a voice application.",
                        "Eine Ressourcenkontolizenz ist zugewiesen, aber das Konto ist mit keiner Sprachanwendung verknüpft."),
                    Text(
                        "Verify the intended association. Reclaim the license only after confirming the account is unused.",
                        "Prüfen Sie die vorgesehene Zuordnung. Geben Sie die Lizenz erst frei, nachdem das Konto als unbenutzt bestätigt wurde."),
                    Destination(account.Kind));
            }
        }
    }

    private static IEnumerable<TenantHealthFinding> EvaluateCallQueuesWithoutAgents(TenantHealthContext context)
    {
        foreach (var queue in context.Topology.CallQueues.Where(queue => queue.HasNoAgents))
        {
            yield return Finding(
                CallQueueWithoutAgents,
                "empty",
                TenantHealthSeverity.Error,
                "callQueue",
                queue.Identity,
                Display(queue.Name, queue.Identity),
                Text(
                    "This call queue has no direct agents and no distribution list, so it cannot ring a person.",
                    "Diese Anrufwarteschleife hat weder direkte Agenten noch eine Verteilerliste und kann daher keine Person anrufen."),
                Text(
                    "Add at least one agent or distribution list, then test the queue with a real call.",
                    "Fügen Sie mindestens einen Agenten oder eine Verteilerliste hinzu und testen Sie die Warteschleife danach mit einem echten Anruf."),
                ConstantsService.Pages.CallQueues);
        }
    }

    private static IEnumerable<TenantHealthFinding> EvaluateAutoAttendantsWithoutAfterHours(TenantHealthContext context)
    {
        var states = context.Enrichment.AutoAttendants.ToDictionary(item => item.Identity, StringComparer.OrdinalIgnoreCase);
        foreach (var attendant in context.Topology.AutoAttendants.Where(attendant =>
                     !states[attendant.Identity].HasAfterHoursHandling))
        {
            yield return Finding(
                AutoAttendantWithoutAfterHours,
                "missing",
                TenantHealthSeverity.Warning,
                "autoAttendant",
                attendant.Identity,
                Display(attendant.Name, attendant.Identity),
                Text(
                    "This auto attendant has no after-hours call-handling association.",
                    "Diese automatische Telefonzentrale hat keine Anrufbehandlung ausserhalb der Geschäftszeiten."),
                Text(
                    "Create an after-hours schedule and call flow, associate them, and verify the closed-hours route.",
                    "Erstellen Sie einen Zeitplan und Anrufablauf ausserhalb der Geschäftszeiten, ordnen Sie beide zu und prüfen Sie den Anrufweg bei geschlossenem Betrieb."),
                ConstantsService.Pages.AutoAttendants);
        }
    }

    private static IEnumerable<TenantHealthFinding> EvaluateUnassignedServiceNumbers(TenantHealthContext context)
    {
        foreach (var number in context.Enrichment.UnassignedServiceNumbers)
        {
            yield return Finding(
                UnassignedServiceNumber,
                "unassigned",
                TenantHealthSeverity.Information,
                "phoneNumber",
                number.TelephoneNumber,
                number.TelephoneNumber,
                Text(
                    $"This active {number.NumberType} number supports voice applications but is not assigned.",
                    $"Diese aktive {number.NumberType}-Rufnummer unterstützt Sprachanwendungen, ist aber nicht zugewiesen."),
                Text(
                    "Assign it to the intended resource account, or release it through your number provider if it is not needed.",
                    "Weisen Sie die Rufnummer dem vorgesehenen Ressourcenkonto zu oder geben Sie sie beim Rufnummernanbieter frei, wenn sie nicht benötigt wird."),
                destinationPage: null);
        }
    }

    private static IEnumerable<TenantHealthFinding> EvaluateResourceAccountUsageLocations(TenantHealthContext context)
    {
        var states = context.Enrichment.ResourceAccounts.ToDictionary(item => item.ObjectId, StringComparer.OrdinalIgnoreCase);
        foreach (var account in context.Topology.ResourceAccounts.Where(account =>
                     string.IsNullOrWhiteSpace(states[account.ObjectId].UsageLocation)))
        {
            yield return Finding(
                ResourceAccountUsageLocation,
                "missing",
                TenantHealthSeverity.Warning,
                "resourceAccount",
                account.ObjectId,
                Display(account.DisplayName, account.UserPrincipalName),
                Text(
                    "This resource account has no usage location.",
                    "Dieses Ressourcenkonto hat keinen Verwendungsstandort."),
                Text(
                    "Set the usage location to the country that matches its license and phone-number assignment.",
                    "Setzen Sie den Verwendungsstandort auf das Land, das zur Lizenz und Rufnummernzuweisung passt."),
                Destination(account.Kind));
        }
    }

    private static TenantHealthFinding Finding(
        string ruleId,
        string variant,
        TenantHealthSeverity severity,
        string objectType,
        string objectId,
        string displayName,
        LocalizedHealthText explanation,
        LocalizedHealthText recommendation,
        string? destinationPage)
        => new(
            $"{ruleId}:{variant}:{objectId}",
            ruleId,
            severity,
            objectType,
            objectId,
            displayName,
            explanation,
            recommendation,
            destinationPage);

    private static HashSet<string> ReferencedResourceAccountIds(TenantTopology topology)
        => topology.AutoAttendants.SelectMany(item => item.ResourceAccountObjectIds)
            .Concat(topology.CallQueues.SelectMany(item => item.ResourceAccountObjectIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? Destination(ResourceAccountKind kind)
        => kind switch
        {
            ResourceAccountKind.AutoAttendant => ConstantsService.Pages.AutoAttendants,
            ResourceAccountKind.CallQueue => ConstantsService.Pages.CallQueues,
            _ => null,
        };

    private static LocalizedHealthText Text(string english, string german) => new(english, german);

    private static string Display(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
