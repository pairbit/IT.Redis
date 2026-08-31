using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StackExchange.Redis;

/// <summary>
/// Represents a key or value that can be stored in redis.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public readonly struct RedisKeyOrValue
{
#pragma warning disable SA1134
    [FieldOffset(0)] private readonly int _index;
    [FieldOffset(4)] private readonly int _length;
    [FieldOffset(8)] private readonly object? _obj;
#pragma warning restore SA1134

    /// <summary>
    /// IsNull.
    /// </summary>
    public bool IsNull => _obj is null;

    /// <summary>
    /// IsKey.
    /// </summary>
    public bool IsKey
    {
        get
        {
            var obj = _obj;
            if (obj is byte[] || obj is string)
            {
                return _index < 0;
            }
            return false;
        }
    }

    /// <summary>
    /// Key.
    /// </summary>
    public RedisKey Key => IsKey ? new RedisKey(null, _obj) : default;

    /// <summary>
    /// IsValue.
    /// </summary>
    public bool IsValue
    {
        get
        {
            var obj = _obj;
            if (obj == null) return false;
            if (obj is byte[] || obj is string)
            {
                return _index >= 0;
            }
            return true;
        }
    }

    /// <summary>
    /// Value.
    /// </summary>
    public RedisValue Value
    {
        get
        {
            if (!IsValue) return default;

            var copy = this;
            return Unsafe.As<RedisKeyOrValue, RedisValue>(ref copy);
        }
    }

    /// <summary>
    /// Key.
    /// </summary>
    /// <param name="key">key.</param>
    public RedisKeyOrValue(in RedisKey key)
    {
        var keyValue = key.KeyValue;
        var keyPrefix = key.KeyPrefix;
        if (keyPrefix != null)
        {
            if (keyValue != null)
                keyPrefix = (byte[]?)key ?? throw new InvalidOperationException("keyPrefix is null");

            _obj = keyPrefix;
            _index = -1;
            _length = keyPrefix.Length;
        }
        else if (keyValue == null)
        {
            this = default;
        }
        else if (keyValue is byte[] bytes)
        {
            _obj = bytes;
            _index = -1;
            _length = bytes.Length;
        }
        else if (keyValue is string str)
        {
            _obj = str;
            _index = -1;
            _length = str.Length;
        }
        else
        {
            throw new ArgumentException("Unrecognized key type", nameof(key));
        }
    }

    /// <summary>
    /// Value.
    /// </summary>
    /// <param name="value">value.</param>
    public RedisKeyOrValue(in RedisValue value)
    {
        var copy = value;
        this = Unsafe.As<RedisValue, RedisKeyOrValue>(ref copy);
    }
}
