using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis.Protocol;

namespace StackExchange.Redis;

/// <content>
/// The scripts commands.
/// </content>
/// <remarks>
/// Here rather than in a single surface-wide class, so that a group is one place. The extension methods
/// bind by namespace, and the namespace is <c>StackExchange.Redis</c>, so this costs a caller nothing.
/// </remarks>
public static partial class Scripts
{
    private static ValueTask<TResult> EvaluateCached<TResult>(
        RespScripts scripts,
        CachedLuaScript script,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespHandler<TResult> handler)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));
        var context = scripts.Context;

        if ((flags & CommandFlags.NoScriptCache) != 0
            || !context.CommandMap.IsAvailable(RedisCommand.SCRIPT)
            || context.Executor is { Accumulates: true })
        {
            // EVAL_RO can be DISABLED in the command map, and the fallback is not just a different name:
            // EVAL_RO defaults to CommandRetryReadOnly where EVAL defaults to CommandRetryWriteAccumulating,
            // so swapping the command alone would quietly make a script the caller asked for read-only
            // retry like a write. ForReadOnlyScript pins the category before it falls back.
            var eval = readOnly
                ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVAL_RO, ref flags)
                : RedisCommand.EVAL;
            var cmd = context.Render($"{eval}{(RedisValue)script.Bytes}{(RedisValue)keys.Length}{keys}{args}");
            return context.SendAsync(ref cmd, flags, handler);
        }

        var preamble = script.AsRequest();
        return Repairable(
            SendPair(context, preamble, (RedisValue)script.Hash, keys, args, flags, readOnly, script, handler),
            context,
            script,
            keys,
            args,
            flags,
            readOnly,
            handler);
    }

    /// <summary>Recover from <c>NOSCRIPT</c> by re-sending the script body.</summary>
    /// <remarks>
    /// <para>
    /// <b>A belief about a server's script cache can always be wrong.</b> <c>SCRIPT FLUSH</c>, a restart,
    /// or a failover to a replica that never saw the load all leave the endpoint believing a script is
    /// present when it is not - and the reply is <c>NOSCRIPT</c>. The shipped surface repairs this in
    /// <c>ResultProcessor</c> and re-issues with the body; without the same repair the new surface turns
    /// an ordinary, expected event into an exception the caller has to handle.
    /// </para>
    /// <para>
    /// <b>Only where the source is known.</b> A caller who supplied a hash and not a script cannot be
    /// repaired - there is nothing to re-send - so <c>EvaluateHash</c> lets the error stand, which is
    /// what the remarks there already say.
    /// </para>
    /// <para>
    /// The belief is dropped before retrying, so the retry carries its own <c>SCRIPT LOAD</c> rather than
    /// trusting the record that has just been proved wrong, and the next caller reloads too.
    /// </para>
    /// </remarks>
    private static ValueTask<TResult> Repairable<TResult>(
        ValueTask<TResult> pending,
        RespContext context,
        CachedLuaScript script,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespHandler<TResult> handler)
    {
        if (pending.IsCompletedSuccessfully) return pending; // the overwhelmingly common case: no state machine

        // the spans cannot cross an await, so the retry's arguments are captured now - only on the path
        // that might need them, which is the one that has already suspended
        return Awaited(pending, context, script, keys.ToArray(), args.ToArray(), flags, readOnly, handler);

        static async ValueTask<TResult> Awaited(
            ValueTask<TResult> pending,
            RespContext context,
            CachedLuaScript script,
            RedisKey[] keys,
            RedisValue[] args,
            CommandFlags flags,
            bool readOnly,
            IRespHandler<TResult> handler)
        {
            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Kind == RedisErrorKind.NoScript)
            {
                var eval = readOnly
                    ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVAL_RO, ref flags)
                    : RedisCommand.EVAL;
                var cmd = context.Render($"{eval}{(RedisValue)script.Bytes}{(RedisValue)keys.Length}{keys}{args}");
                return await context.SendAsync(ref cmd, flags, handler).ConfigureAwait(false);
            }
        }
    }

    /// <summary>EVALSHA_RO, preceded by SCRIPT LOAD; the read-only form of <c>Evaluate</c>.</summary>
    /// <param name="scripts">The scripting command group.</param>
    /// <param name="script">The Lua source; it must not write.</param>
    /// <param name="keys">The keys the script accesses; these route the command.</param>
    /// <param name="args">Everything else the script needs.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>
    /// <para>
    /// A separate method rather than a flag on <c>Evaluate</c>, because it is a separate command with a
    /// different retry category: <c>EVALSHA_RO</c> is read-only, where <c>EVALSHA</c> must be treated as
    /// a write. Hiding that behind a boolean would let a caller's retry semantics change invisibly.
    /// </para>
    /// <para>
    /// <b>Requires a server that has the read-only forms</b> (7.0 and later). The older surface probes
    /// the connection and falls back; this does not, because the frame is chosen before the connection
    /// is - which is the same constraint that puts the loaded-script belief at write time.
    /// </para>
    /// </remarks>
    internal static ValueTask<RespResult> EvaluateReadOnlyAsync(
        this RespScripts scripts,
        CachedLuaScript script,
        ReadOnlySpan<RedisKey> keys = default,
        ReadOnlySpan<RedisValue> args = default,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => EvaluateCached(scripts, script, keys, args, flags, readOnly: true, RespHandlers.Result);

    /// <summary>Render the EVALSHA and send it behind the preamble.</summary>
    private static ValueTask<TResult> SendPair<TResult>(
        RespContext context,
        ref RespRequestFrame preamble,
        RedisValue hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespPreambleGate gate,
        IRespHandler<TResult> handler)
    {
        var command = readOnly
            ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVALSHA_RO, ref flags)
            : RedisCommand.EVALSHA;
        var request = context.Render($"{command}{hash}{(RedisValue)keys.Length}{keys}{args}");
        try
        {
            return context.SendWithPreambleAsync(
                ref preamble, ref request, flags, handler, gate);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Render the EVALSHA and send it behind a preamble the registry already owns.</summary>
    /// <remarks>
    /// The registry's preamble owns nothing poolable - it is a fixed array that is never returned - so
    /// it needs no disposal and can be handed over directly.
    /// </remarks>
    private static ValueTask<TResult> SendPair<TResult>(
        RespContext context,
        RespRequest preamble,
        RedisValue hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespPreambleGate gate,
        IRespHandler<TResult> handler)
    {
        var command = readOnly
            ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVALSHA_RO, ref flags)
            : RedisCommand.EVALSHA;
        var request = context.Render($"{command}{hash}{(RedisValue)keys.Length}{keys}{args}");
        try
        {
            return context.SendWithPreambleAsync(
                preamble, ref request, flags, handler, gate);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>The shipped <see cref="RedisResult"/> shape, for <c>IDatabase.ScriptEvaluate</c>.</summary>
    /// <remarks>
    /// <b>Permanent, not scaffolding</b>, and internal for the same reason the <c>*Array</c> shims are:
    /// <see cref="RedisResult"/> materialises the whole reply, which is what the low-allocation
    /// <see cref="RespResult"/> exists to avoid, so new code must not be able to pick it by accident.
    /// It is the same parse either way - the handler reuses <c>RedisResult.TryCreate</c> - so the two
    /// shapes cannot drift.
    /// </remarks>
    internal static ValueTask<RedisResult> EvaluateCachedResult(
        this RespScripts scripts,
        CachedLuaScript script,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool readOnly,
        CommandFlags flags)
        => EvaluateCached(scripts, script, keys, args, flags, readOnly, RedisResultHandler.Instance);
}
