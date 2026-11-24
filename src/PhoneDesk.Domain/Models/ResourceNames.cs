namespace PhoneDesk.Models
{
    /// <summary>
    /// The naming convention for every object PhoneDesk creates for a customer. One place, so the
    /// desktop configuration model and the headless one always produce the same names.
    /// </summary>
    public static class ResourceNames
    {
        public static string M365Group(string customer, string customerGroupName)
            => $"ttgrp-{customer}-{customerGroupName}";

        public static string CallQueueResourceAccount(string customer, string customerGroupName)
            => $"racq-{customer}-{customerGroupName}";

        public static string CallQueue(string customer, string customerGroupName)
            => $"cq-{customer}-{customerGroupName}";

        public static string AutoAttendantResourceAccount(string customer, string numberName, string customerGroupName)
            => $"raaa-{customer}-{numberName}-{customerGroupName}";

        public static string AutoAttendant(string customer, string numberName, string customerGroupName)
            => $"aa-{customer}-{numberName}-{customerGroupName}";

        public static string Holiday(string customer, string holidayNameSuffix)
            => $"hd-{customer}-{holidayNameSuffix}";
    }
}
