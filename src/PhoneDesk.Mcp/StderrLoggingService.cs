using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using PhoneDesk.Services.Interfaces;
using AppLogLevel = PhoneDesk.Services.LogLevel;

namespace PhoneDesk.Mcp;

/// <summary>
/// Application log for the headless server. Entries go to the host logger, which writes to stderr,
/// because stdout carries the protocol.
/// </summary>
internal sealed class StderrLoggingService : ILoggingService
{
    private const int MaxEntries = 500;

    private readonly ILogger<StderrLoggingService> _logger;
    private readonly Lock _gate = new();
    private readonly List<(string Message, AppLogLevel Level)> _entries = new();

    public StderrLoggingService(ILogger<StderrLoggingService> logger)
    {
        _logger = logger;
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public ObservableCollection<string> LogEntries { get; } = new();

    public string LatestLogEntry { get; private set; } = string.Empty;

    public AppLogLevel MinimumLogLevel { get; set; }

    public void Log(string message, AppLogLevel level = AppLogLevel.Info)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                _entries.RemoveAt(0);
            }

            _entries.Add((message, level));
            LatestLogEntry = message;
        }

        _logger.Log(ToHostLevel(level), "{Message}", message);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            LatestLogEntry = string.Empty;
        }
    }

    public IReadOnlyList<string> GetFilteredEntries()
    {
        lock (_gate)
        {
            return _entries
                .Where(entry => entry.Level >= MinimumLogLevel)
                .Select(entry => entry.Message)
                .ToList();
        }
    }

    private static Microsoft.Extensions.Logging.LogLevel ToHostLevel(AppLogLevel level) => level switch
    {
        AppLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        AppLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Information
    };
}
