using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;

namespace PhoneDesk.Services
{
    public class PowerShellCommandService : IPowerShellCommandService
    {
        private readonly CommonScriptBuilder _commonBuilder;
        private readonly CallQueueScriptBuilder _callQueueBuilder;
        private readonly AutoAttendantScriptBuilder _autoAttendantBuilder;
        private readonly HolidayScriptBuilder _holidayBuilder;
        private readonly ResourceAccountScriptBuilder _resourceAccountBuilder;
        private readonly DashboardScriptBuilder _dashboardBuilder;

        public PowerShellCommandService(
            CommonScriptBuilder commonBuilder,
            CallQueueScriptBuilder callQueueBuilder,
            AutoAttendantScriptBuilder autoAttendantBuilder,
            HolidayScriptBuilder holidayBuilder,
            ResourceAccountScriptBuilder resourceAccountBuilder,
            DashboardScriptBuilder dashboardBuilder)
        {
            _commonBuilder = commonBuilder;
            _callQueueBuilder = callQueueBuilder;
            _autoAttendantBuilder = autoAttendantBuilder;
            _holidayBuilder = holidayBuilder;
            _resourceAccountBuilder = resourceAccountBuilder;
            _dashboardBuilder = dashboardBuilder;
        }

        public string GetRetrieveTenantTopologyCommand() => _dashboardBuilder.GetRetrieveTenantTopologyCommand();

        public string GetCheckModulesCommand() => _commonBuilder.GetCheckModulesCommand();
        public string GetCommonSetupScript() => _commonBuilder.GetCommonSetupScript();
        public string GetConnectTeamsCommand() => _commonBuilder.GetConnectTeamsCommand();
        public string GetConnectGraphCommand() => _commonBuilder.GetConnectGraphWithTokenCommand("");
        public string GetConnectGraphWithTokenCommand(string accessToken) => _commonBuilder.GetConnectGraphWithTokenCommand(accessToken);
        public string GetDisconnectTeamsCommand() => _commonBuilder.GetDisconnectTeamsCommand();
        public string GetDisconnectGraphCommand() => _commonBuilder.GetDisconnectGraphCommand();
        public string GetCreateM365GroupCommand(string groupName) => _commonBuilder.GetCreateM365GroupCommand(groupName);
        public string GetRemoveM365GroupCommand(string groupId, string groupName) => _commonBuilder.GetRemoveM365GroupCommand(groupId, groupName);
        public string GetRetrieveM365GroupsCommand() => _commonBuilder.GetRetrieveM365GroupsCommand();
        public string GetM365GroupIdCommand(string groupName) => _commonBuilder.GetM365GroupIdCommand(groupName);
        public string GetRunIdentityCommand(System.Collections.Generic.IReadOnlyList<PhoneDesk.Operations.RunObject> objects)
            => _commonBuilder.GetRunIdentityCommand(objects);

        public string GetEffectiveObjectName(string name) => _commonBuilder.GetEffectiveObjectName(name);

        public string GetTenantPreflightCommand(System.Collections.Generic.IReadOnlyList<PhoneDesk.Models.IPhoneManagerVariables> entries)
            => _commonBuilder.GetTenantPreflightCommand(entries);

        public string GetImportAudioFileCommand(string filePath) => _commonBuilder.GetImportAudioFileCommand(filePath);
        public string GetImportAudioFileCommand(string filePath, string applicationId) => _commonBuilder.GetImportAudioFileCommand(filePath, applicationId);
        public string GetAssignLicenseCommand(string userId, string skuId) => _commonBuilder.GetAssignLicenseCommand(userId, skuId);

        public string GetRetrieveCallQueuesCommand() => _callQueueBuilder.GetRetrieveCallQueuesCommand();
        public string GetCreateCallQueueCommand(IPhoneManagerVariables variables) => _callQueueBuilder.GetCreateCallQueueCommand(variables);
        public string GetCreateCallQueueCommand(string name, string languageId, string m365GroupId, IPhoneManagerVariables? variables = null) => _callQueueBuilder.GetCreateCallQueueCommand(name, languageId, m365GroupId, variables);
        public string GetAssociateResourceAccountWithCallQueueCommand(string resourceAccountUpn, string callQueueName) => _callQueueBuilder.GetAssociateResourceAccountWithCallQueueCommand(resourceAccountUpn, callQueueName);
        public string GetValidateCallQueueResourceAccountCommand(string racqUpn) => _callQueueBuilder.GetValidateCallQueueResourceAccountCommand(racqUpn);
        public string GetCreateCallTargetCommand(string racqUpn) => _callQueueBuilder.GetCreateCallTargetCommand(racqUpn);
        public string GetRemoveCallQueueCommand(string callQueueName) => _callQueueBuilder.GetRemoveCallQueueCommand(callQueueName);
        public string GetSetCallQueueCallerIdCommand(string callQueueName, string resourceAccountUpn) => _callQueueBuilder.GetSetCallQueueCallerIdCommand(callQueueName, resourceAccountUpn);
        public string GetRemoveResourceAccountCommand(string upn) => _callQueueBuilder.GetRemoveResourceAccountCommand(upn);

        public string GetRetrieveAutoAttendantsCommand() => _autoAttendantBuilder.GetRetrieveAutoAttendantsCommand();
        public string GetCreateAutoAttendantCommand(IPhoneManagerVariables variables) => _autoAttendantBuilder.GetCreateAutoAttendantCommand(variables);
        public string GetVerifyAutoAttendantCommand(string aaDisplayName) => _autoAttendantBuilder.GetVerifyAutoAttendantCommand(aaDisplayName);
        public string GetAttachHolidayToAutoAttendantCommand(string holidayName, string aaDisplayName, string holidayGreetingPrompt) => _autoAttendantBuilder.GetAttachHolidayToAutoAttendantCommand(holidayName, aaDisplayName, holidayGreetingPrompt);
        public string GetCreateSimpleAutoAttendantCommand(IPhoneManagerVariables variables) => _autoAttendantBuilder.GetCreateSimpleAutoAttendantCommand(variables);
        public string GetCreateSimpleAutoAttendantCommand(string aaName, string languageId, string timeZoneId) => _autoAttendantBuilder.GetCreateSimpleAutoAttendantCommand(aaName, languageId, timeZoneId);
        public string GetAssociateResourceAccountWithAutoAttendantCommand(string resourceAccountUpn, string autoAttendantName) => _autoAttendantBuilder.GetAssociateResourceAccountWithAutoAttendantCommand(resourceAccountUpn, autoAttendantName);
        public string GetAssignPhoneNumberToAutoAttendantCommand(string upn, string phoneNumber, string phoneNumberType) => _autoAttendantBuilder.GetAssignPhoneNumberToAutoAttendantCommand(upn, phoneNumber, phoneNumberType);
        public string GetCreateDefaultCallFlowCommand(IPhoneManagerVariables variables) => _autoAttendantBuilder.GetCreateDefaultCallFlowCommand(variables);
        public string GetCreateAfterHoursCallFlowCommand(IPhoneManagerVariables variables) => _autoAttendantBuilder.GetCreateAfterHoursCallFlowCommand(variables);
        public string GetCreateAfterHoursScheduleCommand(IPhoneManagerVariables variables) => _autoAttendantBuilder.GetCreateAfterHoursScheduleCommand(variables);
        public string GetCreateCallHandlingAssociationCommand() => _autoAttendantBuilder.GetCreateCallHandlingAssociationCommand();
        public string GetRemoveAutoAttendantCommand(string autoAttendantName) => _autoAttendantBuilder.GetRemoveAutoAttendantCommand(autoAttendantName);
        public string GetRemoveScheduleCommand(string scheduleName) => _autoAttendantBuilder.GetRemoveScheduleCommand(scheduleName);

        public string GetCreateHolidayCommand(string holidayName, DateTime holidayDate) => _holidayBuilder.GetCreateHolidayCommand(holidayName, holidayDate);
        public string GetCreateHolidaySeriesCommand(string holidayName, List<DateTime> holidayDates) => _holidayBuilder.GetCreateHolidaySeriesCommand(holidayName, holidayDates);
        public string GetCreateHolidaySeriesFromEntriesCommand(string holidayName, IReadOnlyList<IHolidayEntry> holidayEntries) => _holidayBuilder.GetCreateHolidaySeriesFromEntriesCommand(holidayName, holidayEntries);

        public string GetRetrieveResourceAccountsCommand() => _resourceAccountBuilder.GetRetrieveResourceAccountsCommand();
        public string GetCreateResourceAccountCommand(IPhoneManagerVariables variables) => _resourceAccountBuilder.GetCreateResourceAccountCommand(variables);
        public string GetCreateResourceAccountCommand(string upn, string displayName, string appId) => _resourceAccountBuilder.GetCreateResourceAccountCommand(upn, displayName, appId);
        public string GetUpdateResourceAccountUsageLocationCommand(string upn, string usageLocation) => _resourceAccountBuilder.GetUpdateResourceAccountUsageLocationCommand(upn, usageLocation);
        public string GetRetrieveAutoAttendantResourceAccountsCommand() => _resourceAccountBuilder.GetRetrieveAutoAttendantResourceAccountsCommand();
        public string GetCreateAutoAttendantResourceAccountCommand(IPhoneManagerVariables variables) => _resourceAccountBuilder.GetCreateAutoAttendantResourceAccountCommand(variables);
        public string GetCreateAutoAttendantResourceAccountCommand(string upn, string displayName, string appId) => _resourceAccountBuilder.GetCreateAutoAttendantResourceAccountCommand(upn, displayName, appId);
        public string GetUpdateAutoAttendantResourceAccountUsageLocationCommand(string upn, string usageLocation) => _resourceAccountBuilder.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(upn, usageLocation);
        public string GetAssignAutoAttendantLicenseCommand(string userId, string skuId) => _resourceAccountBuilder.GetAssignAutoAttendantLicenseCommand(userId, skuId);
    }
}
