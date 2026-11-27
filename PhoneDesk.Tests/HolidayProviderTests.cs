using PhoneDesk.Holidays;
using PhoneDesk.Models;
using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests;

/// <summary>
/// The holiday lists of the countries beside Switzerland, against dates the authorities publish,
/// and the calendar file reader. The years have the cases a rule can get wrong: a holiday on a
/// weekend, and Easter early and late.
/// </summary>
public class HolidayProviderTests
{
    private static IReadOnlyList<HolidayDate> Holidays(string country, int year, string? region = null)
        => HolidayProviders.Find(country)!.GetHolidays(year, region);

    private static DateTime On(IReadOnlyList<HolidayDate> holidays, string name)
        => holidays.Single(holiday => holiday.Name == name).Date;

    private static VariablesViewModel Create(ViewModelTestHarness h) => new(
        h.PowerShellContextService.Object, h.PowerShellCommandService.Object, h.LoggingService.Object,
        h.SessionManager.Object, h.NavigationService.Object, h.ErrorHandlingService.Object,
        h.ValidationService.Object, h.SharedStateService.Object, h.DialogService.Object);

    [Theory]
    [InlineData("BW", 12)]
    [InlineData("BY", 12)]
    [InlineData("BY-KATH", 13)]
    [InlineData("BY-AUGSBURG", 14)]
    [InlineData("BE", 10)]
    [InlineData("BB", 12)]
    [InlineData("HB", 10)]
    [InlineData("HH", 10)]
    [InlineData("HE", 10)]
    [InlineData("MV", 11)]
    [InlineData("NI", 10)]
    [InlineData("NW", 11)]
    [InlineData("RP", 11)]
    [InlineData("SL", 12)]
    [InlineData("SN", 11)]
    [InlineData("SN-FRONLEICHNAM", 12)]
    [InlineData("ST", 11)]
    [InlineData("SH", 10)]
    [InlineData("TH", 11)]
    [InlineData("TH-FRONLEICHNAM", 12)]
    public void Germany_HasTheHolidaysOfEachState(string region, int count)
    {
        var holidays = Holidays("DE", 2026, region);

        Assert.Equal(count, holidays.Count);
        Assert.All(holidays, holiday => Assert.Equal(HolidayCategory.PublicHoliday, holiday.Category));
        // Easter 2026 is 5 April.
        Assert.Equal(new DateTime(2026, 4, 3), On(holidays, "Karfreitag"));
        Assert.Equal(new DateTime(2026, 5, 14), On(holidays, "Christi Himmelfahrt"));
        Assert.Equal(new DateTime(2026, 5, 25), On(holidays, "Pfingstmontag"));
    }

    [Fact]
    public void Germany_ComputesTheMovingDaysAndNeedsTheState()
    {
        Assert.Equal(new DateTime(2026, 6, 4), On(Holidays("DE", 2026, "NW"), "Fronleichnam"));
        // The Wednesday before 23 November.
        Assert.Equal(new DateTime(2025, 11, 19), On(Holidays("DE", 2025, "SN"), "Buß- und Bettag"));
        Assert.Equal(new DateTime(2026, 11, 18), On(Holidays("DE", 2026, "SN"), "Buß- und Bettag"));
        Assert.Equal(new DateTime(2028, 11, 22), On(Holidays("DE", 2028, "SN"), "Buß- und Bettag"));
        Assert.Equal(new DateTime(2026, 5, 24), On(Holidays("DE", 2026, "BB"), "Pfingstsonntag"));

        Assert.Throws<ArgumentOutOfRangeException>(() => Holidays("DE", 2026));
        Assert.Throws<ArgumentOutOfRangeException>(() => Holidays("DE", 2026, "ZH"));
    }

    [Fact]
    public void AustriaAndFrance_HaveTheirLegalHolidays()
    {
        var austria = Holidays("AT", 2027);
        Assert.Equal(13, austria.Count);
        // Easter 2027 is 28 March.
        Assert.Equal(new DateTime(2027, 3, 29), On(austria, "Ostermontag"));
        Assert.Equal(new DateTime(2027, 5, 27), On(austria, "Fronleichnam"));
        Assert.Equal(new DateTime(2027, 10, 26), On(austria, "Nationalfeiertag"));

        var france = Holidays("FR", 2027);
        Assert.Equal(11, france.Count);
        Assert.Equal(new DateTime(2027, 5, 6), On(france, "Ascension"));
        Assert.Equal(new DateTime(2027, 5, 17), On(france, "Lundi de Pentecôte"));
        var alsace = Holidays("FR", 2027, "ALSACE-MOSELLE");
        Assert.Equal(13, alsace.Count);
        Assert.Equal(new DateTime(2027, 3, 26), On(alsace, "Vendredi saint"));
        Assert.Equal(new DateTime(2027, 12, 26), On(alsace, "Saint-Étienne"));
    }

    [Fact]
    public void UnitedKingdom_AddsTheSubstituteDayForAHolidayOnAWeekend()
    {
        // 2026: Boxing Day is a Saturday.
        var england2026 = Holidays("GB", 2026, "ENG-WLS");
        Assert.Equal(new DateTime(2026, 5, 4), On(england2026, "Early May bank holiday"));
        Assert.Equal(new DateTime(2026, 5, 25), On(england2026, "Spring bank holiday"));
        Assert.Equal(new DateTime(2026, 8, 31), On(england2026, "Summer bank holiday"));
        Assert.Equal(new DateTime(2026, 12, 28), On(england2026, "Boxing Day (substitute day)"));
        Assert.DoesNotContain(england2026, holiday => holiday.Name == "Christmas Day (substitute day)");

        // 2027: Christmas Day is a Saturday and Boxing Day a Sunday.
        var england2027 = Holidays("GB", 2027, "ENG-WLS");
        Assert.Equal(new DateTime(2027, 12, 27), On(england2027, "Christmas Day (substitute day)"));
        Assert.Equal(new DateTime(2027, 12, 28), On(england2027, "Boxing Day (substitute day)"));

        // 2022 and 2033: Christmas Day is a Sunday; Boxing Day keeps its Monday.
        var england2033 = Holidays("GB", 2033, "ENG-WLS");
        Assert.Equal(new DateTime(2033, 12, 27), On(england2033, "Christmas Day (substitute day)"));
        Assert.DoesNotContain(england2033, holiday => holiday.Name == "Boxing Day (substitute day)");

        // Scotland 2028: 1 January is a Saturday and 2 January a Sunday.
        var scotland = Holidays("GB", 2028, "SCT");
        Assert.Equal(new DateTime(2028, 1, 3), On(scotland, "New Year's Day (substitute day)"));
        Assert.Equal(new DateTime(2028, 1, 4), On(scotland, "2 January (substitute day)"));
        Assert.Equal(new DateTime(2028, 8, 7), On(scotland, "Summer bank holiday"));
        Assert.DoesNotContain(scotland, holiday => holiday.Name == "Easter Monday");

        // Northern Ireland 2026: 12 July is a Sunday.
        var northernIreland = Holidays("GB", 2026, "NIR");
        Assert.Equal(new DateTime(2026, 7, 13), On(northernIreland, "Battle of the Boyne (Orangemen's Day) (substitute day)"));
        Assert.Equal(new DateTime(2026, 3, 17), On(northernIreland, "St Patrick's Day"));
    }

    [Fact]
    public void UnitedStates_AddsTheObservedDayForAHolidayOnAWeekend()
    {
        var y2026 = Holidays("US", 2026);
        Assert.Equal(new DateTime(2026, 1, 19), On(y2026, "Birthday of Martin Luther King, Jr."));
        Assert.Equal(new DateTime(2026, 2, 16), On(y2026, "Washington's Birthday"));
        Assert.Equal(new DateTime(2026, 5, 25), On(y2026, "Memorial Day"));
        Assert.Equal(new DateTime(2026, 9, 7), On(y2026, "Labor Day"));
        Assert.Equal(new DateTime(2026, 10, 12), On(y2026, "Columbus Day"));
        Assert.Equal(new DateTime(2026, 11, 26), On(y2026, "Thanksgiving Day"));
        // 4 July 2026 is a Saturday.
        Assert.Equal(new DateTime(2026, 7, 3), On(y2026, "Independence Day (observed)"));

        var y2027 = Holidays("US", 2027);
        Assert.Equal(new DateTime(2027, 6, 18), On(y2027, "Juneteenth National Independence Day (observed)"));
        Assert.Equal(new DateTime(2027, 7, 5), On(y2027, "Independence Day (observed)"));
        Assert.Equal(new DateTime(2027, 12, 24), On(y2027, "Christmas Day (observed)"));
        // 1 January 2028 is a Saturday: it is observed on the last day of 2027.
        Assert.Equal(new DateTime(2027, 12, 31), On(y2027, "New Year's Day (observed)"));
        Assert.DoesNotContain(Holidays("US", 2028), holiday => holiday.Name == "New Year's Day (observed)");
    }

    [Fact]
    public void Switzerland_IsTheCatalogItAlwaysWas()
    {
        var provider = HolidayProviders.Find("CH")!;
        var expected = SwissHolidayCatalog.Compute(SwissCanton.ZH, null, 2026).Holidays;
        var fribourg = SwissHolidayCatalog.Compute(SwissCanton.FR, "fr-catholic", 2026).Holidays;

        Assert.Equal(expected.Select(h => (h.Date, h.Name, h.Category)), provider.GetHolidays(2026, "ZH").Select(h => (h.Date, h.Name, h.Category)));
        Assert.Equal(fribourg.Select(h => (h.Date, h.Name)), provider.GetHolidays(2026, "FR/fr-catholic").Select(h => (h.Date, h.Name)));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.GetHolidays(2026, null));
    }

    private const string Calendar =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270101\r\nDTEND;VALUE=DATE:20270102\r\nSUMMARY:Nieuwjaarsdag\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20271227\r\nDTEND;VALUE=DATE:20280103\r\nSUMMARY:Betriebsferien\\, Standort Nord und\r\n  Süd\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;TZID=\"Europe/Zurich:x\":20271224T120000\r\nDTEND;TZID=Europe/Zurich:20271224T180000\r\nSUMMARY:Heiligabend Nachmittag\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART:20270501T060000Z\r\nDTEND:20270501T160000Z\r\nSUMMARY:Tag der Arbeit\r\nRRULE:FREQ=YEARLY\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270815\r\nSUMMARY:Abgesagt\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Ohne Datum\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20280417\r\nSUMMARY:Ostermontag 2028\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Fact]
    public void CalendarFile_IsReadAsHolidayEntries()
    {
        var result = IcsHolidayImport.Parse(Calendar);

        Assert.Equal(5, result.Holidays.Count);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, line => line.StartsWith("Abgesagt", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, line => line.StartsWith("Ohne Datum", StringComparison.Ordinal));

        var newYear = result.Holidays[0].Entry;
        Assert.Equal(new DateTime(2027, 1, 1), newYear.DateTime);
        Assert.False(newYear.HasEndDate);

        // A range of whole days ends at the start of the day after; a folded line and an escaped comma are joined.
        var closure = result.Holidays.Single(holiday => holiday.Entry.Name!.StartsWith("Betriebsferien", StringComparison.Ordinal)).Entry;
        Assert.Equal("Betriebsferien, Standort Nord und Süd", closure.Name);
        Assert.Equal(new DateTime(2027, 12, 27), closure.DateTime);
        Assert.Equal(new DateTime(2028, 1, 3), closure.EndDateTime);

        // A timed event keeps its times; a colon inside a quoted parameter is not the end of the name.
        var afternoon = result.Holidays.Single(holiday => holiday.Entry.Name == "Heiligabend Nachmittag").Entry;
        Assert.Equal(new DateTime(2027, 12, 24, 12, 0, 0), afternoon.DateTime);
        Assert.Equal(new DateTime(2027, 12, 24, 18, 0, 0), afternoon.EndDateTime);

        var repeating = result.Holidays.Single(holiday => holiday.Entry.Name == "Tag der Arbeit");
        Assert.Contains("repeats", repeating.Note);
        Assert.Contains("UTC", repeating.Note);

        // Only the events of a year, with a range that reaches into it.
        Assert.Equal(new[] { "Betriebsferien, Standort Nord und Süd", "Ostermontag 2028" }, IcsHolidayImport.Parse(Calendar, 2028).Holidays.Select(h => h.Entry.Name));
        Assert.Empty(IcsHolidayImport.Parse("not a calendar").Holidays);
    }

    [Fact]
    public void Picker_OffersTheCountriesAndAddsOnlyTheDatesLeftIn()
    {
        var vm = Create(new ViewModelTestHarness());
        vm.OpenPredefinedHolidaysWizardCommand.Execute(null);
        Assert.Equal(new[] { "Switzerland", "Germany", "Austria", "France", "United Kingdom", "United States" }, vm.Countries);

        vm.SelectedYear = 2026;
        vm.SelectedCountry = "Germany";
        Assert.False(vm.ShowSwissHolidayOptions);
        Assert.True(vm.HasProviderRegions);
        // The state decides: nothing is offered before it is chosen.
        Assert.False(vm.CanApplyPredefinedSelection);
        Assert.Empty(vm.PredefinedHolidayPreview);

        vm.SelectedProviderRegion = vm.ProviderRegions.Single(region => region.Code == "BY");
        Assert.True(vm.CanApplyPredefinedSelection);
        Assert.Equal(12, vm.PredefinedHolidayPreview.Count);
        Assert.All(vm.PredefinedHolidayPreview, entry => Assert.True(entry.IsPublicHoliday));

        // The customer is open on Epiphany.
        vm.PredefinedHolidayPreview.Single(entry => entry.Name == "Heilige Drei Könige").IsIncluded = false;
        vm.ApplyPredefinedHolidaysCommand.Execute(null);
        Assert.Equal(11, vm.Variables.HolidaySeries.Count);
        Assert.DoesNotContain(vm.Variables.HolidaySeries, entry => entry.Name == "Heilige Drei Könige");

        // A country with one main list shows it at once.
        vm.OpenPredefinedHolidaysWizardCommand.Execute(null);
        vm.SelectedYear = 2027;
        vm.SelectedCountry = "France";
        Assert.Equal("METROPOLE", vm.SelectedProviderRegion?.Code);
        Assert.Equal(11, vm.PredefinedHolidayPreview.Count);
        vm.SelectedCountry = "Austria";
        Assert.False(vm.HasProviderRegions);
        Assert.Equal(13, vm.PredefinedHolidayPreview.Count);

        // The category survives the configuration file.
        var service = new TenantAsCodeService();
        var json = service.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(vm.Variables));
        var roundTrip = PhoneManagerVariablesPortabilityMapper.FromDocument(service.DeserializeConfiguration(json));
        Assert.All(roundTrip.HolidaySeries, entry => Assert.True(entry.IsPublicHoliday));
    }

    [Fact]
    public void Picker_TakesTheEventsOfACalendarFileForTheSelectedYear()
    {
        var vm = Create(new ViewModelTestHarness());
        vm.OpenPredefinedHolidaysWizardCommand.Execute(null);
        vm.SelectedYear = 2027;

        vm.LoadHolidayCalendar(Calendar);

        Assert.True(vm.IsCalendarFileLoaded);
        Assert.False(vm.ShowSwissHolidayOptions);
        Assert.True(vm.CanApplyPredefinedSelection);
        Assert.Equal(4, vm.PredefinedHolidayPreview.Count);
        Assert.True(vm.HasHolidaySourceNote);

        vm.PredefinedHolidayPreview.Single(entry => entry.Name == "Tag der Arbeit").IsIncluded = false;
        vm.ApplyPredefinedHolidaysCommand.Execute(null);
        Assert.Equal(3, vm.Variables.HolidaySeries.Count);
        var closure = vm.Variables.HolidaySeries.Single(entry => entry.Name!.StartsWith("Betriebsferien", StringComparison.Ordinal));
        Assert.Equal(new DateTime(2028, 1, 3), closure.EndDate);

        // Removing the file brings the canton fields back.
        vm.OpenPredefinedHolidaysWizardCommand.Execute(null);
        Assert.False(vm.IsCalendarFileLoaded);
        Assert.True(vm.ShowSwissHolidayOptions);
    }
}
