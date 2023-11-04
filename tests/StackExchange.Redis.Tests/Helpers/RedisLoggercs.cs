using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;

namespace StackExchange.Redis.Tests.Helpers;

internal class RedisLogger : ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    public readonly static Func<Type, ILogger> NullFactory = GetFactory(NullLoggerFactory.Instance);

    public RedisLogger(Microsoft.Extensions.Logging.ILogger logger)
        => _logger = logger;

    public void Log(int level, Exception? exception, string? message)
        => _logger.Log((LogLevel)level, exception, message);

    public static Func<Type, ILogger> GetFactory(ILoggerFactory factory) =>
        type => new RedisLogger(factory.CreateLogger(type));
}
