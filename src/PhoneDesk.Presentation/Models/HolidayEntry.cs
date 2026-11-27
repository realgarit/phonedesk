using CommunityToolkit.Mvvm.ComponentModel;
using PhoneDesk.Holidays;
using System.Text.Json.Serialization;

namespace PhoneDesk.Models
{
    public partial class HolidayEntry : ObservableObject, IHolidayEntry
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStatutory))]
        [NotifyPropertyChangedFor(nameof(IsAdditionalCantonalRestDay))]
        [NotifyPropertyChangedFor(nameof(IsSundayObservance))]
        [NotifyPropertyChangedFor(nameof(IsCustom))]
        [NotifyPropertyChangedFor(nameof(IsPublicHoliday))]
        private HolidayCategory _category;

        [JsonIgnore] public bool IsPublicHoliday => Category == HolidayCategory.PublicHoliday;

        /// <summary>In a list of suggested dates: false when the operator left this one out. Not saved.</summary>
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isIncluded = true;

        [JsonIgnore] public bool IsStatutory => Category == HolidayCategory.Statutory;
        [JsonIgnore] public bool IsAdditionalCantonalRestDay => Category == HolidayCategory.AdditionalCantonalRestDay;
        [JsonIgnore] public bool IsSundayObservance => Category == HolidayCategory.SundayObservance;
        [JsonIgnore] public bool IsCustom => Category == HolidayCategory.Custom;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private DateTime _date = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private TimeSpan _time = new TimeSpan(9, 0, 0); // Default to 9:00 AM

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private string? _name; // Holiday name for predefined sets

        /// <summary>
        /// Optional end date for multi-day holidays. When null, Teams defaults to next day at 12:00 PM.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private DateTime? _endDate;

        /// <summary>
        /// Optional end time. Only used when EndDate is set.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayText))]
        private TimeSpan? _endTime;

        public HolidayEntry()
        {
        }

        public HolidayEntry(DateTime date, TimeSpan time)
        {
            Date = date;
            Time = time;
        }

        public HolidayEntry(DateTime date, TimeSpan time, string? name)
        {
            Date = date;
            Time = time;
            Name = name;
        }

        public HolidayEntry(DateTime date, TimeSpan time, string? name, DateTime? endDate, TimeSpan? endTime)
        {
            Date = date;
            Time = time;
            Name = name;
            EndDate = endDate;
            EndTime = endTime;
        }

        public DateTime DateTime => Date.Date.Add(Time);

        /// <summary>
        /// End DateTime. Only meaningful when EndDate is set.
        /// </summary>
        public DateTime? EndDateTime => EndDate?.Date.Add(EndTime ?? TimeSpan.Zero);

        /// <summary>
        /// Whether this holiday has an explicit end date/time configured.
        /// </summary>
        public bool HasEndDate => EndDate.HasValue;

        public string DisplayText
        {
            get
            {
                var baseText = string.IsNullOrEmpty(Name) 
                    ? $"{Date:dd.MM.yyyy} {Time:hh\\:mm}" 
                    : $"{Date:dd.MM.yyyy} {Time:hh\\:mm} - {Name}";
                
                if (HasEndDate)
                {
                    baseText += $" (bis {EndDate!.Value:dd.MM.yyyy} {EndTime ?? TimeSpan.Zero:hh\\:mm})";
                }
                
                return baseText;
            }
        }
    }
}
