using System;

namespace StackExchange.Redis;

/// <summary>
/// Disposing the lua script request data.
/// </summary>
public interface IScriptRequestDisposer
{
    /// <summary>
    /// Disposing the lua script request data.
    /// </summary>
    /// <param name="args">lua script request.</param>
    void Dispose(ReadOnlyMemory<RedisKeyOrValue> args);
}
