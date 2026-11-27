using PhoneDesk.Models;

namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Service for building PowerShell command strings.
/// </summary>
public interface IPowerShellCommandService
{
    // Dashboard (read-only tenant topology, issue #64)
    string GetRetrieveTenantTopologyCommand();

    // Common commands
    string GetCheckModulesCommand();
    string GetCommonSetupScript();
    string GetConnectTeamsCommand();
    string GetConnectGraphCommand();
    string GetConnectGraphWithTokenCommand(string accessToken);
    string GetDisconnectTeamsCommand();
    string GetDisconnectGraphCommand();
    string GetImportAudioFileCommand(string filePath);
    string GetImportAudioFileCommand(string filePath, string applicationId);

    /// <summary>
    /// The read-only script that compares the entries of a dry-run plan with the tenant. It looks
    /// for the names as the provisioning scripts write them.
    /// </summary>
    string GetTenantPreflightCommand(System.Collections.Generic.IReadOnlyList<PhoneDesk.Models.IPhoneManagerVariables> entries);
    string GetAssignLicenseCommand(string userId, string skuId);

    // M365 Groups
    string GetCreateM365GroupCommand(string groupName);
    string GetRemoveM365GroupCommand(string groupId, string groupName);
    string GetRetrieveM365GroupsCommand();
    string GetM365GroupIdCommand(string groupName);

    // Call Queue operations
    string GetRetrieveCallQueuesCommand();
    string GetCreateCallQueueCommand(IPhoneManagerVariables variables);
    string GetCreateCallQueueCommand(string name, string languageId, string m365GroupId, IPhoneManagerVariables? variables = null);
    string GetAssociateResourceAccountWithCallQueueCommand(string resourceAccountUpn, string callQueueName);
    string GetValidateCallQueueResourceAccountCommand(string racqUpn);
    string GetCreateCallTargetCommand(string racqUpn);
    string GetRemoveCallQueueCommand(string callQueueName);
    string GetSetCallQueueCallerIdCommand(string callQueueName, string resourceAccountUpn);
    string GetRemoveResourceAccountCommand(string upn);

    // Auto Attendant operations
    string GetRetrieveAutoAttendantsCommand();
    string GetCreateAutoAttendantCommand(IPhoneManagerVariables variables);
    string GetCreateSimpleAutoAttendantCommand(IPhoneManagerVariables variables);
    string GetCreateSimpleAutoAttendantCommand(string aaName, string languageId, string timeZoneId);
    string GetVerifyAutoAttendantCommand(string aaDisplayName);
    string GetAttachHolidayToAutoAttendantCommand(string holidayName, string aaDisplayName, string holidayGreetingPrompt);
    string GetAssociateResourceAccountWithAutoAttendantCommand(string resourceAccountUpn, string autoAttendantName);
    string GetAssignPhoneNumberToAutoAttendantCommand(string upn, string phoneNumber, string phoneNumberType);
    string GetCreateDefaultCallFlowCommand(IPhoneManagerVariables variables);
    string GetCreateAfterHoursCallFlowCommand(IPhoneManagerVariables variables);
    string GetCreateAfterHoursScheduleCommand(IPhoneManagerVariables variables);
    string GetCreateCallHandlingAssociationCommand();
    string GetRemoveAutoAttendantCommand(string autoAttendantName);
    string GetRemoveScheduleCommand(string scheduleName);

    // Holiday operations
    string GetCreateHolidayCommand(string holidayName, DateTime holidayDate);
    string GetCreateHolidaySeriesCommand(string holidayName, List<DateTime> holidayDates);
    string GetCreateHolidaySeriesFromEntriesCommand(string holidayName, IReadOnlyList<IHolidayEntry> holidayEntries);

    // Resource Account operations
    string GetRetrieveResourceAccountsCommand();
    string GetRetrieveAutoAttendantResourceAccountsCommand();
    string GetCreateResourceAccountCommand(IPhoneManagerVariables variables);
    string GetCreateResourceAccountCommand(string upn, string displayName, string appId);
    string GetCreateAutoAttendantResourceAccountCommand(IPhoneManagerVariables variables);
    string GetCreateAutoAttendantResourceAccountCommand(string upn, string displayName, string appId);
    string GetUpdateResourceAccountUsageLocationCommand(string upn, string usageLocation);
    string GetUpdateAutoAttendantResourceAccountUsageLocationCommand(string upn, string usageLocation);
    string GetAssignAutoAttendantLicenseCommand(string userId, string skuId);
}
