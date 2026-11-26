using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Language;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.ScriptBuilders;
using Xunit;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Parses the scripts PhoneDesk sends to PowerShell with the real PowerShell parser. A script
    /// with a syntax error fails on the operator's tenant before any cmdlet runs (for example the
    /// unreplaced "{sanitizedRacqUPN}" and "@{{SkuId = $SkuId}}" text in the Guided Setup call queue step).
    /// </summary>
    public class GeneratedScriptSyntaxTests
    {
        private static PowerShellCommandService CreateCommands()
        {
            var san = new PowerShellSanitizationService();
            return new PowerShellCommandService(
                new CommonScriptBuilder(san),
                new CallQueueScriptBuilder(san),
                new AutoAttendantScriptBuilder(san),
                new HolidayScriptBuilder(san),
                new ResourceAccountScriptBuilder(san),
                new DashboardScriptBuilder());
        }

        private static PhoneManagerVariables MinimalVariables() => new()
        {
            Customer = "ewp",
            CustomerGroupName = "hauptnummer",
            MsFallbackDomain = "elektroweberpartner.onmicrosoft.com",
            RaaAnrName = "haupt",
            LanguageId = "de-DE",
            TimeZoneId = "W. Europe Standard Time",
            UsageLocation = "CH",
            RaaAnr = "+41441234567",
            PhoneNumberType = "DirectRouting",
        };

        private static PhoneManagerVariables FullVariables()
        {
            var vars = MinimalVariables();
            vars.CqGreetingType = "TextToSpeech";
            vars.CqGreetingTextToSpeechPrompt = "Willkommen";
            vars.AaDefaultGreetingType = "TextToSpeech";
            vars.AaDefaultGreetingTextToSpeechPrompt = "Guten Tag";
            vars.AaDefaultAction = "TransferToTarget";
            vars.AaDefaultActionTarget = vars.RacqUPN;
            vars.AaAfterHoursGreetingType = "TextToSpeech";
            vars.AaAfterHoursGreetingTextToSpeechPrompt = "Wir sind geschlossen";
            vars.AaAfterHoursAction = "Disconnect";
            vars.OpeningHours1Start = new TimeSpan(8, 0, 0);
            vars.OpeningHours1End = new TimeSpan(12, 0, 0);
            vars.OpeningHours2Start = new TimeSpan(13, 30, 0);
            vars.OpeningHours2End = new TimeSpan(17, 0, 0);
            return vars;
        }

        public static IEnumerable<object[]> GuidedSetupScripts()
        {
            var commands = CreateCommands();
            foreach (var (label, vars) in new[] { ("minimal", MinimalVariables()), ("full", FullVariables()) })
            {
                yield return new object[] { $"{label}: M365 group", commands.GetCreateM365GroupCommand(vars.M365Group) };
                yield return new object[] { $"{label}: CQ resource account", commands.GetCreateResourceAccountCommand(vars) };
                yield return new object[] { $"{label}: CQ usage location", commands.GetUpdateResourceAccountUsageLocationCommand(vars.RacqUPN, vars.UsageLocation) };
                yield return new object[] { $"{label}: CQ license", commands.GetAssignLicenseCommand(vars.RacqUPN, vars.SkuId) };
                yield return new object[] { $"{label}: call queue", commands.GetCreateCallQueueCommand(vars) };
                yield return new object[] { $"{label}: AA resource account", commands.GetCreateAutoAttendantResourceAccountCommand(vars) };
                yield return new object[] { $"{label}: AA license", commands.GetAssignAutoAttendantLicenseCommand(vars.RaaaUPN, vars.SkuId) };
                yield return new object[] { $"{label}: AA phone number", commands.GetAssignPhoneNumberToAutoAttendantCommand(vars.RaaaUPN, vars.RaaAnr, vars.PhoneNumberType) };
                yield return new object[] { $"{label}: auto attendant", commands.GetCreateAutoAttendantCommand(vars) };
                yield return new object[] { $"{label}: AA association", commands.GetAssociateResourceAccountWithAutoAttendantCommand(vars.RaaaUPN, vars.AaDisplayName) };
                yield return new object[] { $"{label}: caller ID", commands.GetSetCallQueueCallerIdCommand(vars.CqDisplayName, vars.RaaaUPN) };
            }
        }

        [Theory]
        [MemberData(nameof(GuidedSetupScripts))]
        public void GuidedSetupScript_ParsesWithoutErrors(string label, string script)
        {
            AssertParses(label, script);
        }

        [Fact]
        public void DeleteScripts_ParseAndUseCmdletsThatExist()
        {
            var commands = CreateCommands();
            var resourceAccount = commands.GetRemoveResourceAccountCommand("racq-ewp-hauptnummer@contoso.onmicrosoft.com");
            var autoAttendant = commands.GetRemoveAutoAttendantCommand("aa-ewp-haupt-hauptnummer");
            var callQueue = commands.GetRemoveCallQueueCommand("cq-ewp-hauptnummer");

            AssertParses("remove resource account", resourceAccount);
            AssertParses("remove auto attendant", autoAttendant);
            AssertParses("remove call queue", callQueue);

            // The MicrosoftTeams module has no Remove-CsOnlineUser; resource accounts are deleted through Graph.
            Assert.DoesNotContain("Remove-CsOnlineUser", resourceAccount, StringComparison.Ordinal);
            Assert.Contains("Remove-MgUser", resourceAccount, StringComparison.Ordinal);
            // Schedules are removed by ID only; their names are shared across auto attendants.
            Assert.Contains("Remove-CsOnlineSchedule -Id $scheduleId", autoAttendant, StringComparison.Ordinal);
            Assert.DoesNotContain("After Hours Schedule", autoAttendant, StringComparison.Ordinal);
        }

        [Fact]
        public void ReleasePhoneNumberScript_ParsesAndRejectsAMalformedNumber()
        {
            var script = PhoneDesk.Operations.PhoneNumberScripts.GetReleaseUnassignedPhoneNumberCommand("+41441234567");

            Parser.ParseInput(script, out _, out var errors);

            Assert.Empty(errors);
            Assert.Contains("Remove-CsOnlineTelephoneNumber -TelephoneNumber \"+41441234567\" -ErrorAction Stop", script);
            Assert.Throws<ArgumentException>(() => PhoneDesk.Operations.PhoneNumberScripts.GetReleaseUnassignedPhoneNumberCommand("+4144\"; Remove-Item"));
            Assert.Throws<ArgumentException>(() => PhoneDesk.Operations.PhoneNumberScripts.GetReleaseUnassignedPhoneNumberCommand("0441234567"));
        }

        [Fact]
        public void BulkScript_ParsesWithoutErrors()
        {
            var san = new PowerShellSanitizationService();
            var bulk = new BulkOperationsScriptBuilder(CreateCommands(), san);

            AssertParses("bulk", bulk.GenerateBulkScript(new List<PhoneManagerVariables> { MinimalVariables(), FullVariables() }));
        }

        [Fact]
        public void CallQueueStep_OnlyCreatesTheCallQueueAndAssociation()
        {
            var script = CreateCommands().GetCreateCallQueueCommand(MinimalVariables());

            // The resource account and its license are separate Guided Setup steps.
            Assert.DoesNotContain("New-CsOnlineApplicationInstance ", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Set-MgUserLicense", script, StringComparison.Ordinal);
            Assert.Contains("New-CsCallQueue", script, StringComparison.Ordinal);
            Assert.Contains("New-CsOnlineApplicationInstanceAssociation", script, StringComparison.Ordinal);
            Assert.Contains("racq-ewp-hauptnummer@elektroweberpartner.onmicrosoft.com", script, StringComparison.Ordinal);
            Assert.Contains("ttgrp-ewp-hauptnummer", script, StringComparison.Ordinal);
        }

        [Fact]
        public void AutoAttendantStep_OnlyCreatesTheAutoAttendant()
        {
            var script = CreateCommands().GetCreateAutoAttendantCommand(FullVariables());

            // Resource account, license, phone number and association are separate Guided Setup steps.
            Assert.DoesNotContain("New-CsOnlineApplicationInstance", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Set-MgUserLicense", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Set-CsPhoneNumberAssignment", script, StringComparison.Ordinal);
            Assert.Contains("New-CsAutoAttendant ", script, StringComparison.Ordinal);
            Assert.Contains("13:30", script, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("TransferToCallQueue")]
        public void AutoAttendantStep_SendsOpeningHoursCallsToTheCallQueue(string? defaultAction)
        {
            // Field report: a Guided Setup with default values built an auto attendant that hung up
            // during opening hours, although the same run created the call queue.
            var vars = MinimalVariables();
            vars.AaDefaultAction = defaultAction;
            var script = CreateCommands().GetCreateAutoAttendantCommand(vars);
            AssertParses("auto attendant to call queue", script);

            Assert.Contains("$defaultCallQueueAccount = Get-CsOnlineUser \"racq-ewp-hauptnummer@elektroweberpartner.onmicrosoft.com\"", script, StringComparison.Ordinal);
            Assert.Contains("$defaultCallTarget = New-CsAutoAttendantCallableEntity -Identity $defaultCallQueueAccount.Identity -Type ApplicationEndpoint", script, StringComparison.Ordinal);
            Assert.Contains("$defaultMenuOption = New-CsAutoAttendantMenuOption -Action TransferCallToTarget", script, StringComparison.Ordinal);
            Assert.Contains("Create the call queue first", script, StringComparison.Ordinal);
            // Outside opening hours nothing changes: no action still means the call ends.
            Assert.Contains("$afterhoursMenuOption = New-CsAutoAttendantMenuOption -Action DisconnectCall", script, StringComparison.Ordinal);
            Assert.DoesNotContain("$afterhoursCallQueueAccount", script, StringComparison.Ordinal);
        }

        [Fact]
        public void AutoAttendantStep_HangsUpInOpeningHoursOnlyWhenDisconnectIsSelected()
        {
            var vars = MinimalVariables();
            vars.AaDefaultAction = "Disconnect";
            var script = CreateCommands().GetCreateAutoAttendantCommand(vars);

            Assert.Contains("$defaultMenuOption = New-CsAutoAttendantMenuOption -Action DisconnectCall", script, StringComparison.Ordinal);
            Assert.DoesNotContain("CallQueueAccount", script, StringComparison.Ordinal);
        }

        [Fact]
        public void NewConfiguration_SendsOpeningHoursCallsToTheCallQueue()
        {
            Assert.Equal(PhoneDesk.Operations.ConfigurationChecks.TransferToCallQueue, new PhoneManagerVariables().AaDefaultAction);
        }

        [Fact]
        public void LastStep_SetsTheCallerIdOfTheCallQueueOnlyWhenSelected()
        {
            var commands = CreateCommands();
            var factory = new PhoneDesk.Operations.GuidedSetupScriptFactory(commands);
            var vars = MinimalVariables();
            const int lastStep = (int)PhoneDesk.Operations.GuidedSetupStep.AssociateAutoAttendantResourceAccount;

            var without = factory.GetProvisioningScript(lastStep, vars);
            Assert.Equal(commands.GetAssociateResourceAccountWithAutoAttendantCommand(vars.RaaaUPN, vars.AaDisplayName), without);
            Assert.DoesNotContain("OboResourceAccountIds", without, StringComparison.Ordinal);

            vars.CqUseAutoAttendantNumberAsCallerId = true;
            var with = factory.GetProvisioningScript(lastStep, vars);
            AssertParses("last step with caller ID", with);
            Assert.StartsWith(without, with, StringComparison.Ordinal);
            Assert.Contains("Get-CsOnlineUser \"raaa-ewp-haupt-hauptnummer@elektroweberpartner.onmicrosoft.com\"", with, StringComparison.Ordinal);
            Assert.Contains("Set-CsCallQueue -Identity $cqCallerIdQueue.Identity -OboResourceAccountIds @($cqCallerIdAccount.Identity) -ErrorAction Stop", with, StringComparison.Ordinal);
            Assert.Contains("cq-ewp-hauptnummer", with, StringComparison.Ordinal);
            // The call queue step itself stays as it was: the account does not exist yet at that point.
            Assert.DoesNotContain("OboResourceAccountIds", commands.GetCreateCallQueueCommand(vars), StringComparison.Ordinal);
        }

        [Fact]
        public void AutoAttendantStep_UnusedSecondRange_IsNotCreated()
        {
            // Live tenant: New-CsOnlineTimeRange rejects 00:00-00:00 even when the range is not used afterwards.
            var vars = MinimalVariables();
            vars.OpeningHours1Start = new TimeSpan(8, 0, 0);
            vars.OpeningHours1End = new TimeSpan(17, 0, 0);

            var script = CreateCommands().GetCreateAutoAttendantCommand(vars);

            Assert.Contains("-Start \"08:00\" -End \"17:00\"", script, StringComparison.Ordinal);
            Assert.DoesNotContain("$aatr2", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\"00:00\"", script, StringComparison.Ordinal);
            AssertParses("auto attendant", script);
        }

        [Fact]
        public void AutoAttendantStep_UsesTheDayPlanWhenSelected()
        {
            var vars = MinimalVariables();
            vars.UsePerDaySchedule = true;
            foreach (var day in vars.WeeklySchedule)
            {
                day.IsEnabled = day.DayName is "Monday" or "Saturday";
            }

            var script = CreateCommands().GetCreateAutoAttendantCommand(vars);

            Assert.Contains("-MondayHours $mondayHours", script, StringComparison.Ordinal);
            Assert.Contains("-SaturdayHours $saturdayHours", script, StringComparison.Ordinal);
            Assert.DoesNotContain("-TuesdayHours", script, StringComparison.Ordinal);
            AssertParses("auto attendant day plan", script);
        }

        private static void AssertParses(string label, string script)
        {
            Parser.ParseInput(script, out _, out var errors);

            Assert.True(
                errors.Length == 0,
                $"{label} has PowerShell syntax errors:\n" + string.Join("\n", errors.Select(e => $"line {e.Extent.StartLineNumber}: {e.Message}")));
            Assert.DoesNotContain("{sanitized", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@{{", script, StringComparison.Ordinal);
            Assert.DoesNotContain("{callQueueParams}", script, StringComparison.Ordinal);
        }
    }
}
