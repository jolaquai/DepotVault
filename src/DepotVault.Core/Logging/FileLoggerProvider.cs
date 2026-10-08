using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DepotVault.Core.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 10L << 20;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly string _dir;
    private readonly LogLevel _min;
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _dir = directory;
        _min = minLevel;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string CurrentFile => Path.Combine(_dir, "depotvault.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, static (n, p) => new FileLogger(p, n), this);

    private async Task WriteLoopAsync()
    {
        var path = CurrentFile;
        var sb = new StringBuilder();
        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            sb.Clear();
            while (_channel.Reader.TryRead(out var line))
                sb.Append(line);
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, Path.Combine(_dir, "depotvault.1.log"), true);
                await File.AppendAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
            }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(logLevel)}] {_short}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}";
            provider._channel.Writer.TryWrite(line);
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
