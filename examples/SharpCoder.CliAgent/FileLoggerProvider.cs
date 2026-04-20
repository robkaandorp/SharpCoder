using Microsoft.Extensions.Logging;

namespace SharpCoder.CliAgent;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that writes formatted log lines to a
/// single <see cref="StreamWriter"/> shared across all loggers it creates.
/// The writer is flushed after every write so the log stays readable live
/// while a run is in progress, and fully flushed + closed on Dispose.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Information)
    {
        _writer = new StreamWriter(filePath, append: true) { AutoFlush = false };
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void WriteLine()
    {
        lock (_gate)
        {
            _writer.WriteLine();
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{timestamp}] [{logLevel,-11}] [{_category}] {message}";
            _provider.WriteLine(line);

            if (exception != null)
            {
                _provider.WriteLine(exception.ToString());
            }
        }
    }
}
