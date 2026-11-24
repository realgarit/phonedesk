using System;
using System.Linq;
using PhoneDesk.Services;
using PhoneDesk.Topology;

namespace PhoneDesk.Tests
{
    public class TenantTopologyAssemblerTests
    {
        private const string Sample =
            "SUCCESS: connected\n" +
            "TOPRA: RA CQ One|racq-one@contoso.com|ra-obj-1|+41 21 000|CallQueue|True\n" +
            "TOPRA: RA AA One|raaa-one@contoso.com|ra-obj-2|+41 21 111|AutoAttendant|True\n" +
            "TOPRA: Orphan RA|racq-orphan@contoso.com|ra-obj-3||CallQueue|True\n" +
            "TOPRA: Disabled RA|racq-dis@contoso.com|ra-obj-4|+41 21 999|CallQueue|False\n" +
            "TOPAA: Main AA|aa-id-1|en-US|W. Europe Standard Time|ra-obj-2|sched-1,sched-2|ra-obj-1\n" +
            "TOPCQ: Support CQ|cq-id-1|Attendant|30|agent-1|grp-1|ra-obj-1\n" +
            "TOPCQ: Empty CQ|cq-id-2|Serial|20|||ra-obj-9\n" +
            "TOPGRP: Support Group|grp-1|support|Support team\n" +
            "SUCCESS: Tenant topology retrieved\n";

        private readonly TenantTopologyAssembler _sut = new();

        [Fact]
        public void Assemble_KeepsTheObjectIdOfAGroupWhoseNameContainsTheSeparator()
        {
            // A display name with "|" must not shift the fields: the ID would otherwise be part of the
            // name, and two such groups could get the same wrong ID.
            var t = _sut.Assemble(
                "TOPGRP: Sales | North|0f3c5a1e-7b2d-4c8f-9a61-2d4e6f8a0b1c|sales-north|Team | North region\n" +
                "TOPGRP: Sales | South|7d9e1f20-3a4b-4c5d-8e6f-0a1b2c3d4e5f|sales-south|\n",
                DateTimeOffset.UtcNow);

            Assert.Equal(2, t.Groups.Count);
            Assert.Equal("Sales | North", t.Groups[0].DisplayName);
            Assert.Equal("0f3c5a1e-7b2d-4c8f-9a61-2d4e6f8a0b1c", t.Groups[0].Id);
            Assert.Equal("sales-north", t.Groups[0].MailNickname);
            Assert.Equal("Team | North region", t.Groups[0].Description);
            Assert.Equal("7d9e1f20-3a4b-4c5d-8e6f-0a1b2c3d4e5f", t.Groups[1].Id);
        }

        [Fact]
        public void Assemble_ParsesEveryObjectType()
        {
            var t = _sut.Assemble(Sample, DateTimeOffset.UtcNow);

            Assert.Equal(4, t.ResourceAccounts.Count);
            Assert.Single(t.AutoAttendants);
            Assert.Equal(2, t.CallQueues.Count);
            Assert.Single(t.Groups);
        }

        [Fact]
        public void Assemble_MapsResourceAccountFields()
        {
            var t = _sut.Assemble(Sample, DateTimeOffset.UtcNow);

            var ra = t.ResourceAccounts.Single(r => r.ObjectId == "ra-obj-1");
            Assert.Equal("RA CQ One", ra.DisplayName);
            Assert.Equal("racq-one@contoso.com", ra.UserPrincipalName);
            Assert.Equal("+41 21 000", ra.PhoneNumber);
            Assert.Equal(ResourceAccountKind.CallQueue, ra.Kind);
            Assert.True(ra.AccountEnabled);

            var disabled = t.ResourceAccounts.Single(r => r.ObjectId == "ra-obj-4");
            Assert.False(disabled.AccountEnabled);

            var orphan = t.ResourceAccounts.Single(r => r.ObjectId == "ra-obj-3");
            Assert.Null(orphan.PhoneNumber);
            Assert.False(orphan.HasPhoneNumber);
        }

        [Fact]
        public void Assemble_ParsesCsvSubfields()
        {
            var t = _sut.Assemble(Sample, DateTimeOffset.UtcNow);

            var aa = t.AutoAttendants.Single();
            Assert.Equal(new[] { "ra-obj-2" }, aa.ResourceAccountObjectIds);
            Assert.Equal(new[] { "sched-1", "sched-2" }, aa.HolidayScheduleIds);
            Assert.Equal(new[] { "ra-obj-1" }, aa.CallTargetObjectIds);

            var cq = t.CallQueues.Single(c => c.Identity == "cq-id-1");
            Assert.Equal(30, cq.AgentAlertTime);
            Assert.Equal(new[] { "agent-1" }, cq.AgentObjectIds);
            Assert.Equal(new[] { "grp-1" }, cq.DistributionListIds);
        }

        [Fact]
        public void Assemble_EmptyCsvFields_YieldEmptyLists()
        {
            var t = _sut.Assemble(Sample, DateTimeOffset.UtcNow);

            var empty = t.CallQueues.Single(c => c.Identity == "cq-id-2");
            Assert.Empty(empty.AgentObjectIds);
            Assert.Empty(empty.DistributionListIds);
            Assert.True(empty.HasNoAgents);
        }

        [Fact]
        public void Assemble_RunsOrphanDetection()
        {
            var t = _sut.Assemble(Sample, DateTimeOffset.UtcNow);

            // ra-obj-3 and ra-obj-4 are unassociated (referenced set is ra-obj-2, ra-obj-1, ra-obj-9).
            Assert.Equal(2, t.Orphans.Count(o => o.Kind == OrphanKind.ResourceAccountUnassociated));
            // Empty CQ has no agents.
            Assert.Contains(t.Orphans, o => o.Kind == OrphanKind.CallQueueWithoutAgents && o.EntityId == "cq-id-2");
            // Disabled RA with a phone number.
            Assert.Contains(t.Orphans, o => o.Kind == OrphanKind.PhoneNumberOnDisabledAccount && o.EntityId == "ra-obj-4");
        }

        [Fact]
        public void Assemble_IgnoresDiagnosticAndUnknownLines()
        {
            var t = _sut.Assemble("TOPERR: Groups: boom\nrandom noise\nTOPGRP: G|g1|nick|desc\n", DateTimeOffset.UtcNow);

            Assert.Empty(t.AutoAttendants);
            Assert.Single(t.Groups);
        }

        [Fact]
        public void Assemble_NullOrEmpty_ReturnsEmptyTopology()
        {
            var t = _sut.Assemble(null, DateTimeOffset.UtcNow);
            Assert.Empty(t.ResourceAccounts);
            Assert.Empty(t.Orphans);
        }

        [Fact]
        public void Assemble_HandlesCrlfLineEndings()
        {
            var t = _sut.Assemble("TOPGRP: G|g1|nick|desc\r\nTOPGRP: H|g2|nick2|desc2\r\n", DateTimeOffset.UtcNow);
            Assert.Equal(2, t.Groups.Count);
        }

        [Fact]
        public void Assemble_PreservesRetrievedTimestamp()
        {
            var when = DateTimeOffset.UtcNow;
            var t = _sut.Assemble(Sample, when);
            Assert.Equal(when, t.RetrievedAtUtc);
        }
    }
}
