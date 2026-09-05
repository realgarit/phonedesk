using System.Collections.ObjectModel;
using PhoneDesk.Models;
using PhoneDesk.Portability;
using PhoneDesk.Services;

namespace PhoneDesk.Tests;

public sealed class PhoneManagerVariablesPortabilityMapperTests
{
    [Fact]
    public void EverySettableVariableRoundTripsThroughThePortableDocument()
    {
        var original = CreateFullyPopulatedVariables();

        var document = PhoneManagerVariablesPortabilityMapper.ToDocument(original);
        var restored = PhoneManagerVariablesPortabilityMapper.FromDocument(document);

        foreach (var property in typeof(PhoneManagerVariables).GetProperties()
                     .Where(property => property.CanRead && property.CanWrite)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (property.Name is nameof(PhoneManagerVariables.WeeklySchedule) or nameof(PhoneManagerVariables.HolidaySeries))
            {
                continue;
            }

            Assert.Equal(property.GetValue(original), property.GetValue(restored));
        }

        Assert.Equal(
            original.WeeklySchedule.Select(DescribeDay),
            restored.WeeklySchedule.Select(DescribeDay));
        Assert.Equal(
            original.HolidaySeries.Select(DescribeHoliday),
            restored.HolidaySeries.Select(DescribeHoliday));
        Assert.Equal(original.M365Group, restored.M365Group);
        Assert.Equal(original.RacqUPN, restored.RacqUPN);
        Assert.Equal(original.AaDisplayName, restored.AaDisplayName);
    }

    [Fact]
    public void MappingUsesTheExplicitSchemaAndDoesNotExportComputedNames()
    {
        var variables = CreateFullyPopulatedVariables();
        var service = new TenantAsCodeService();

        var json = service.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(variables));

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"autoAttendantTemplate\"", json);
        Assert.Contains("\"callQueueTemplate\"", json);
        Assert.Contains("\"holidayTemplate\"", json);
        Assert.DoesNotContain("racqDisplayName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raaaDisplayName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aaDisplayName", json, StringComparison.OrdinalIgnoreCase);
    }

    private static PhoneManagerVariables CreateFullyPopulatedVariables()
    {
        var variables = new PhoneManagerVariables();
        foreach (var property in typeof(PhoneManagerVariables).GetProperties()
                     .Where(property => property.CanWrite))
        {
            if (property.Name is nameof(PhoneManagerVariables.WeeklySchedule) or nameof(PhoneManagerVariables.HolidaySeries))
            {
                continue;
            }

            object? value = property.PropertyType switch
            {
                var type when type == typeof(string) => $"{property.Name}-value",
                var type when type == typeof(int?) => 37,
                var type when type == typeof(bool) => true,
                var type when type == typeof(DateTime) => new DateTime(2027, 4, 5, 0, 0, 0, DateTimeKind.Unspecified),
                var type when type == typeof(TimeSpan) => new TimeSpan(14, 30, 0),
                _ => throw new InvalidOperationException(
                    $"Add a test value for {property.Name} ({property.PropertyType.Name})."),
            };
            property.SetValue(variables, value);
        }

        variables.WeeklySchedule = new ObservableCollection<DaySchedule>
        {
            new("Monday")
            {
                IsEnabled = true,
                Hours1Start = new TimeSpan(8, 0, 0),
                Hours1End = new TimeSpan(12, 0, 0),
                Hours2Start = new TimeSpan(13, 0, 0),
                Hours2End = new TimeSpan(17, 0, 0),
                HasSecondRange = true,
            },
            new("Tuesday", isEnabled: false),
            new("Wednesday", isEnabled: false),
            new("Thursday", isEnabled: false),
            new("Friday", isEnabled: false),
            new("Saturday", isEnabled: false),
            new("Sunday", isEnabled: false)
            {
                Hours1Start = new TimeSpan(9, 0, 0),
                Hours1End = new TimeSpan(10, 0, 0),
                Hours2Start = new TimeSpan(0, 0, 0),
                Hours2End = new TimeSpan(0, 0, 0),
                HasSecondRange = false,
            },
        };
        variables.HolidaySeries = new ObservableCollection<HolidayEntry>
        {
            new(
                new DateTime(2027, 12, 24),
                new TimeSpan(12, 0, 0),
                "Christmas Eve",
                new DateTime(2027, 12, 27),
                new TimeSpan(8, 0, 0)),
            new(new DateTime(2027, 1, 1), TimeSpan.Zero, "New Year"),
        };
        return variables;
    }

    private static object DescribeDay(DaySchedule day)
        => new
        {
            day.DayName,
            day.IsEnabled,
            day.Hours1Start,
            day.Hours1End,
            day.Hours2Start,
            day.Hours2End,
            day.HasSecondRange,
        };

    private static object DescribeHoliday(HolidayEntry entry)
        => new
        {
            entry.Date,
            entry.Time,
            entry.Name,
            entry.EndDate,
            entry.EndTime,
        };
}
