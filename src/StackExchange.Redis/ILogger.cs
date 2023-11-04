using System;

namespace StackExchange.Redis;

/// <summary>
/// Logger for Redis
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Log
    /// </summary>
    void Log(int level, Exception? exception, string? message);
}
