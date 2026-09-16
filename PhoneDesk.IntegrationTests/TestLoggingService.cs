using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests;

internal sealed class TestLoggingService : ILoggingService
{
    private readonly List<(string Message, LogLevel Level)> _entries = new();
    private string _latestLogEntry = string.Empty;
    private LogLevel _minimumLogLevel;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogEntries { get; } = new();

    public IReadOnlyList<(string Message, LogLevel Level)> Entries => _entries;

    public string LatestLogEntry
    {
        get => _latestLogEntry;
        private set
        {
            if (_latestLogEntry == value)
            {
                return;
            }

            _latestLogEntry = value;
            OnPropertyChanged();
        }
    }

    public LogLevel MinimumLogLevel
    {
        get => _minimumLogLevel;
        set
        {
            if (_minimumLogLevel == value)
            {
                return;
            }

            _minimumLogLevel = value;
            OnPropertyChanged();
        }
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        _entries.Add((message, level));
        LogEntries.Add(message);
        LatestLogEntry = message;
    }

    public void Clear()
    {
        _entries.Clear();
        LogEntries.Clear();
        LatestLogEntry = string.Empty;
    }

    public IReadOnlyList<string> GetFilteredEntries()
        => _entries
            .Where(entry => entry.Level >= MinimumLogLevel)
            .Select(entry => entry.Message)
            .ToList();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
