using System;
using System.Collections.Generic;
using System.Linq;

namespace PhoneDesk.Planning
{
    /// <summary>
    /// The kind of tenant object a <see cref="PlannedObject"/> describes. Purely descriptive metadata for
    /// the dry-run preview — it never drives script generation and has no effect on what is executed.
    /// </summary>
    public enum PlannedObjectType
    {
        M365Group,
        ResourceAccount,
        License,
        PhoneNumber,
        CallQueue,
        AutoAttendant,
        Association
    }

    /// <summary>The action a <see cref="PlannedObject"/> would perform when the plan is executed.</summary>
    public enum PlannedAction
    {
        Create,
        Assign,
        Associate
    }

    /// <summary>Outcome of a preflight check evaluated while building the plan.</summary>
    public enum PreflightStatus
    {
        /// <summary>The check passed.</summary>
        Pass,

        /// <summary>The check surfaced a non-fatal concern the operator should be aware of.</summary>
        Warning,

        /// <summary>The check failed; executing as-is would very likely error.</summary>
        Fail,

        /// <summary>
        /// The check could not be evaluated in this build (e.g. a live-tenant lookup that is not performed
        /// during preview). Reported honestly rather than silently claiming success.
        /// </summary>
        NotChecked
    }

    /// <summary>A single resolved setting on a planned object (e.g. "UPN" → "racq-contoso-...").</summary>
    public sealed record PlannedSetting(string Name, string Value);

    /// <summary>A single object that would be created/changed for one entry, with its resolved settings.</summary>
    public sealed record PlannedObject(
        PlannedObjectType Type,
        PlannedAction Action,
        string DisplayName,
        string? Upn,
        IReadOnlyList<PlannedSetting> Settings)
    {
        /// <summary>Human-readable one-line summary, e.g. "Create M365Group: ttgrp-contoso-hauptnummer".</summary>
        public string Summary => $"{Action} {Type}: {DisplayName}";
    }

    /// <summary>A preflight check result attached to an entry.</summary>
    public sealed record PreflightCheck(string Name, PreflightStatus Status, string Detail)
    {
        public bool IsPass => Status == PreflightStatus.Pass;

        public bool IsWarning => Status == PreflightStatus.Warning;

        public bool IsFail => Status == PreflightStatus.Fail;

        public bool IsNotChecked => Status == PreflightStatus.NotChecked;
    }

    /// <summary>
    /// One complete setup in the plan. For the wizard this is the single configuration under review; for
    /// bulk operations there is one entry per CSV row. Carries the objects that would be created, the
    /// validation errors found in the source data, and the preflight checks that were evaluated.
    /// </summary>
    public sealed record DryRunEntry(
        int RowNumber,
        string Label,
        IReadOnlyList<PlannedObject> Objects,
        IReadOnlyList<string> ValidationErrors,
        IReadOnlyList<PreflightCheck> PreflightChecks)
    {
        /// <summary>True when the entry has no validation errors and no failing preflight checks.</summary>
        public bool IsValid =>
            ValidationErrors.Count == 0 &&
            !PreflightChecks.Any(c => c.Status == PreflightStatus.Fail);

        /// <summary>Comma-joined validation errors for compact display/export.</summary>
        public string ValidationSummary => string.Join("; ", ValidationErrors);
    }

    /// <summary>
    /// An immutable, structured description of what a run <b>would</b> create or change, produced purely from
    /// the same configuration inputs the script builders consume. Generating or exporting a plan performs no
    /// tenant mutation and executes no PowerShell — it is a read-only projection used for review, approval,
    /// and export. It deliberately does not carry any generated script text so it can never diverge from,
    /// or influence, what the frozen builders emit at execution time.
    /// </summary>
    public sealed record DryRunPlan(
        string Source,
        DateTimeOffset GeneratedUtc,
        IReadOnlyList<DryRunEntry> Entries)
    {
        public int EntryCount => Entries.Count;
        public int ValidEntryCount => Entries.Count(e => e.IsValid);
        public int InvalidEntryCount => Entries.Count(e => !e.IsValid);
        public int TotalObjectCount => Entries.Sum(e => e.Objects.Count);

        /// <summary>True when every entry in the plan is valid (safe to execute without skipping rows).</summary>
        public bool IsFullyValid => Entries.Count > 0 && Entries.All(e => e.IsValid);

        /// <summary>The entries that would actually execute when invalid rows are skipped.</summary>
        public IReadOnlyList<DryRunEntry> ValidEntries => Entries.Where(e => e.IsValid).ToList();
    }
}
