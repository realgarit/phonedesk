using System;
using System.Collections.Generic;
using System.Linq;

namespace PhoneDesk.Holidays
{
    /// <summary>
    /// A part of a canton with its own holiday list. <see cref="Code"/> is the value
    /// <see cref="SwissHolidayCatalog.Compute"/> accepts as its district argument.
    /// </summary>
    public sealed record SwissHolidayRegion(string Code, string Name, string Covers);

    /// <summary>
    /// The regions of the cantons whose holidays differ inside the canton (Aargau, Fribourg,
    /// Solothurn). A caller without a picker (the MCP server) uses this list to offer the valid
    /// values and to reject an unknown one, because the catalog answers an unknown region with
    /// the canton-wide minimum only.
    /// </summary>
    public static class SwissHolidayRegions
    {
        private static readonly IReadOnlyList<SwissHolidayRegion> None = Array.Empty<SwissHolidayRegion>();

        // The Aargau groups follow the cantonal holiday table, not the district borders: some
        // municipalities use the list of a neighbouring district.
        private static readonly IReadOnlyList<SwissHolidayRegion> Aargau = new[]
        {
            new SwissHolidayRegion("aarau", "Aarau",
                "Districts of Aarau, Brugg, Kulm, Lenzburg and Zofingen. In the district of Baden: only Bergdietikon."),
            new SwissHolidayRegion("baden", "Baden",
                "District of Baden without Bergdietikon."),
            new SwissHolidayRegion("bremgarten", "Bremgarten",
                "District of Bremgarten."),
            new SwissHolidayRegion("muri", "Muri",
                "Districts of Muri and Laufenburg. In the district of Rheinfelden: only Hellikon, Mumpf, Obermumpf, Schupfart, Stein and Wegenstetten."),
            new SwissHolidayRegion("rheinfelden", "Rheinfelden",
                "In the district of Rheinfelden: only Kaiseraugst, Magden, Möhlin, Olsberg, Rheinfelden, Wallbach, Zeiningen and Zuzgen."),
            new SwissHolidayRegion("zurzach", "Zurzach",
                "District of Zurzach."),
        };

        private static readonly IReadOnlyList<SwissHolidayRegion> Fribourg = new[]
        {
            new SwissHolidayRegion("fr-catholic", "Catholic legal territory",
                "All localities that are not listed under fr-reformed, including Wünnewil-Flamatt without Flamatt and Sensebrügg."),
            new SwissHolidayRegion("fr-reformed", "Reformed legal territory",
                "Courgevaux, Fräschels, Greng, Kerzers, Meyriez, Muntelier, Murten, Ried bei Kerzers, Ulmiz, Mont-Vully, and the localities Flamatt and Sensebrügg in Wünnewil-Flamatt."),
        };

        private static readonly IReadOnlyList<SwissHolidayRegion> Solothurn = new[]
        {
            new SwissHolidayRegion("so-bucheggberg", "District of Bucheggberg",
                "District of Bucheggberg."),
            new SwissHolidayRegion("so-other", "Other districts",
                "All districts except Bucheggberg."),
        };

        /// <summary>The regions of a canton; empty when one list applies to the whole canton.</summary>
        public static IReadOnlyList<SwissHolidayRegion> For(SwissCanton canton) => canton switch
        {
            SwissCanton.AG => Aargau,
            SwissCanton.FR => Fribourg,
            SwissCanton.SO => Solothurn,
            _ => None,
        };

        /// <summary>Finds a region of the canton by its code or name, ignoring case.</summary>
        public static SwissHolidayRegion? Find(SwissCanton canton, string? value)
        {
            var wanted = value?.Trim();
            if (string.IsNullOrEmpty(wanted))
                return null;

            return For(canton).FirstOrDefault(region =>
                string.Equals(region.Code, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(region.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }
    }
}
