using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace StackExchange.Redis;

internal sealed class TextWriterLogger : ILogger
{
    private TextWriter? _writer;
    private readonly ILogger? _wrapped;

    public TextWriterLogger(TextWriter writer, ILogger? wrapped)
    {
        _writer = writer;
        _wrapped = wrapped;
    }

    public void Log(int logLevel, Exception? exception, string? message)
    {
        _wrapped?.Log(logLevel, exception, message);
        if (_writer is TextWriter writer)
        {
            lock (writer)
            {
                // We check here again because it's possible we've released below, and never want to write past releasing.
                if (_writer is TextWriter innerWriter)
                {
                    innerWriter.Write($"{DateTime.UtcNow:HH:mm:ss.ffff}: ");
                    innerWriter.WriteLine(GetMessage(logLevel, exception, message));
                }
            }
        }
    }

    public void Release()
    {
        // We lock here because we may have piled up on a lock above and still be writing.
        // We never want a write to go past the Release(), as many TextWriter implementations are not thread safe.
        if (_writer is TextWriter writer)
        {
            lock (writer)
            {
                _writer = null;
            }
        }
    }

    public static string GetMessage(int level, Exception? exception, string? message)
        => exception != null ? $"[{(LogLevel)level}] {message} -> {exception.Message} -> {exception.StackTrace}"
                             : $"[{(LogLevel)level}] {message}";
}

internal static class TextWriterLoggerExtensions
{
    internal static ILogger? With(this ILogger? logger, TextWriter? writer) =>
        writer is not null ? new TextWriterLogger(writer, logger) : logger;
}
