using StackExchange.Redis;
using System;

namespace Microsoft.Extensions.Logging;

internal enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

internal static class XILogger
{
    public static void LogInformation(this ILogger logger, string? message)
        => logger.Log((int)LogLevel.Information, null, message);

    public static void LogTrace(this ILogger logger, string? message)
        => logger.Log((int)LogLevel.Trace, null, message);

    public static void LogInformation(this ILogger logger, Exception? exception, string? message)
        => logger.Log((int)LogLevel.Information, exception, message);

    public static void LogError(this ILogger logger, Exception? exception, string? message)
        => logger.Log((int)LogLevel.Error, exception, message);
}

internal static class xILoggerFactory
{
    public static ILogger CreateLogger<T>(this Func<Type, ILogger> factory)
        => factory(typeof(T));
}
