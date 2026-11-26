using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RESPite;
using RESPite.Messages;
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
    /// <summary>EVALSHA, preceded by SCRIPT LOAD so the hash is certain to resolve.</summary>
    /// <param name="scripts">The scripting command group.</param>
    /// <param name="script">The Lua source.</param>
    /// <param name="keys">The keys the script accesses; these route the command.</param>
    /// <param name="args">Everything else the script needs.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>
    /// <para>
    /// The hash is computed here rather than taken from the server's reply, which is what lets the body
    /// go on the wire <b>once</b>. The existing path cannot do that - it has no hash until
    /// <c>SCRIPT LOAD</c> answers, so its first call sends <c>SCRIPT LOAD</c> and then <c>EVAL</c>,
    /// carrying the body twice.
    /// </para>
    /// <para>
    /// <b>Every key the script touches must be passed in <paramref name="keys"/>.</b> That is the
    /// server's rule rather than ours - it is how a script routes in a cluster - and it is also what
    /// lets a client-side cache know what to invalidate. A script that reaches a key it did not declare
    /// is already broken before caching enters the picture.
    /// </para>
    /// </remarks>
    public static ValueTask<RespResult> EvaluateAsync(
        this RespScripts scripts,
        string script,
        ReadOnlySpan<RedisKey> keys = default,
        ReadOnlySpan<RedisValue> args = default,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => Evaluate(scripts, script, keys, args, flags, readOnly: false, RespHandlers.Result, cancellationToken);

    private static ValueTask<TResult> Evaluate<TResult>(
        RespScripts scripts,
        string script,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespHandler<TResult> handler,
        CancellationToken cancellationToken)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));

        var context = scripts.Context;

        // NoScriptCache means exactly what it says about the protocol: send the body, every time, and
        // take no hash. The script still lands in the server's cache - nothing a client sends can stop
        // that - but in the pool it evicts from, which is the point. It is also why such a script is not
        // admitted to the registry below: the caller has told us it is not worth keeping.
        //
        // A command map without SCRIPT means the SAME thing, and the shipped surface makes exactly that
        // equivalence: there is no way to put the script in the server's cache, so there is no hash worth
        // holding and the body has to travel. Asking only about the flag left the preamble path rendering
        // a SCRIPT LOAD that the map forbids.
        // An ACCUMULATING executor - a batch or a transaction - is the third case that has to carry the
        // body, and for a harder reason than the other two. Such an executor cannot pair, so the preamble
        // falls back to being sent first and AWAITED; but nothing leaves an accumulating executor until the
        // run does, so that await waits for a send this very call is holding up. It surfaced as a script
        // inside a transaction reporting "this transaction has already been executed" - the body being
        // written after EXEC had come and gone - rather than the server error the script actually returned.
        //
        // The shipped core has always sent the body inside MULTI, and ScriptLoadPairingTests says why in
        // its own terms: a SCRIPT LOAD paired inside a transaction would shift every EXEC result.
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
            var cmd = context.Render($"{eval}{script.AsRedisValue()}{(RedisValue)keys.Length}{keys}{args}");
            return context.SendAsync(ref cmd, flags, handler, cancellationToken);
        }

        var registry = context.ScriptCache;
        if (registry is null)
        {
            // no registry: render the preamble afresh, which is correct and wasteful
            var hash = Sha1Hex(script);
            var fresh = context.Render($"{RedisCommand.SCRIPT}{RespLiterals.Load}{script.AsRedisValue()}");
            try
            {
                return SendPair(context, ref fresh, script, hash, keys, args, flags, readOnly, new ScriptLoadGate(script, hash), handler, cancellationToken);
            }
            finally
            {
                fresh.Dispose();
            }
        }

        var preamble = registry.GetPreamble(context, script, out var known, out var gate);
        return SendPair(context, preamble, script, known, keys, args, flags, readOnly, gate, handler, cancellationToken);
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
    public static ValueTask<RespResult> EvaluateReadOnlyAsync(
        this RespScripts scripts,
        string script,
        ReadOnlySpan<RedisKey> keys = default,
        ReadOnlySpan<RedisValue> args = default,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => Evaluate(scripts, script, keys, args, flags, readOnly: true, RespHandlers.Result, cancellationToken);

    /// <summary>Render the EVALSHA and send it behind the preamble.</summary>
    private static ValueTask<TResult> SendPair<TResult>(
        RespContext context,
        ref RespRequestFrame preamble,
        string script,
        string hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespPreambleGate gate,
        IRespHandler<TResult> handler,
        CancellationToken cancellationToken)
    {
        var evalsha = readOnly
            ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVALSHA_RO, ref flags)
            : RedisCommand.EVALSHA;

        var scriptLength = new RedisValue(script).GetByteCount();
        Span<byte> scriptLengthUtf8 = stackalloc byte[10];
        var scriptLengthUtf8Written = Format.FormatInt32(scriptLength, scriptLengthUtf8);
        var offset = scriptLengthUtf8Written + scriptLength - 45;

        // Allocating additional space for the Eval command.
        var builder = new RespRequestBuilder(0, 4, context, evalsha, offset: offset > 0 ? offset : 0);
        builder.AppendFormatted(hash.AsRedisValue());
        builder.AppendFormatted((RedisValue)keys.Length);
        builder.AppendFormatted(keys);
        builder.AppendFormatted(args);

        // We do not return the request to the pool, because we will reuse it in the event that the script is not found.
        // Upon receiving a request, an external user can return all the leased memory associated with the keys and values.
        var request = builder.Build(noReturn: true, offset > 0 ? offset : 0, out var requestHeaderLength);

        // a copy of the request, so as not to lose the reference to the buffer.
        var requestCopy = request;
        var pending = context.SendWithPreambleAsync(ref preamble, ref request, flags, handler, gate, cancellationToken);

        return Awaited(pending, context, script, requestCopy, offset, requestHeaderLength, flags, handler, cancellationToken);
    }

    /// <summary>Render the EVALSHA and send it behind a preamble the registry already owns.</summary>
    /// <remarks>
    /// The registry's preamble owns nothing poolable - it is a fixed array that is never returned - so
    /// it needs no disposal and can be handed over directly.
    /// </remarks>
    private static ValueTask<TResult> SendPair<TResult>(
        RespContext context,
        RespRequest preamble,
        string script,
        string hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        CommandFlags flags,
        bool readOnly,
        IRespPreambleGate gate,
        IRespHandler<TResult> handler,
        CancellationToken cancellationToken)
    {
        var evalsha = readOnly
            ? ForReadOnlyScript(context.CommandMap, RedisCommand.EVALSHA_RO, ref flags)
            : RedisCommand.EVALSHA;

        var scriptLength = new RedisValue(script).GetByteCount();
        Span<byte> scriptLengthUtf8 = stackalloc byte[10];
        var scriptLengthUtf8Written = Format.FormatInt32(scriptLength, scriptLengthUtf8);
        var offset = scriptLengthUtf8Written + scriptLength - 45;

        // The EVALSHA_RO command is 10 bytes long and takes up 2 characters, unlike the EVAL_RO command, which takes up 1 character.
        if (evalsha == RedisCommand.EVALSHA_RO)
            offset--;

        // Allocating additional space for the Eval command.
        var builder = new RespRequestBuilder(0, 4, context, evalsha, offset: offset > 0 ? offset : 0);
        builder.AppendFormatted(hash.AsRedisValue());
        builder.AppendFormatted((RedisValue)keys.Length);
        builder.AppendFormatted(keys);
        builder.AppendFormatted(args);

        // We do not return the request to the pool, because we will reuse it in the event that the script is not found.
        // Upon receiving a request, an external user can return all the leased memory associated with the keys and values.
        var request = builder.Build(noReturn: true, offset > 0 ? offset : 0, out var requestHeaderLength);

        // a copy of the request, so as not to lose the reference to the buffer.
        var requestCopy = request;
        var pending = context.SendWithPreambleAsync(preamble, ref request, flags, handler, gate, cancellationToken);

        return Awaited(pending, context, script, requestCopy, offset, requestHeaderLength, flags, handler, cancellationToken);
    }

    private static async ValueTask<TResult> Awaited<TResult>(
        ValueTask<TResult> pending,
        RespContext context,
        string script,
        RespRequestFrame request,
        int offset,
        int requestHeaderLength,
        CommandFlags flags,
        IRespHandler<TResult> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Kind == RedisErrorKind.NoScript)
        {
            context.ScriptCache?.Forget(script);

            var eval = request.CopyForEval(script, offset, requestHeaderLength);

            pending = context.SendAsync(ref eval, flags, handler, cancellationToken);
        }
        finally
        {
            // the only place where we return the buffer to the pool, to avoid doing so twice
            request.EnsureDispose();
        }

        return await pending.ConfigureAwait(false);
    }

    /// <summary>The SHA1 of a script, lower-case hex, as the server would report it.</summary>
    /// <remarks>
    /// Computed rather than learned so that <c>EVALSHA</c> can be written before <c>SCRIPT LOAD</c> has
    /// answered - the whole reason the body travels once instead of twice.
    /// </remarks>
    internal static string Sha1Hex(string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script);
#if NET
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(bytes, digest);
#else
        using var sha = SHA1.Create();
        var digest = sha.ComputeHash(bytes);
#endif

        // one path for every target: lower-case hex is what the server reports, and `ToHexStringLower`
        // is too new to use here. Not hot - and once the rendered SCRIPT LOAD frame is cached, this
        // runs once per script rather than once per call.
        const string Hex = "0123456789abcdef";
        var chars = new char[40];
        for (var i = 0; i < 20; i++)
        {
            chars[i * 2] = Hex[digest[i] >> 4];
            chars[(i * 2) + 1] = Hex[digest[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>
    /// EVALSHA against a hash the caller already holds, with no <c>SCRIPT LOAD</c> preamble.
    /// </summary>
    /// <param name="scripts">The scripting command group.</param>
    /// <param name="hash">The script's SHA1, as <c>SCRIPT LOAD</c> returned it.</param>
    /// <param name="keys">The keys the script accesses; these route the command.</param>
    /// <param name="args">Everything else the script needs.</param>
    /// <param name="readOnly">Whether to use the read-only form, which requires 7.0 or later.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>
    /// <b>No preamble, and that is the caller's problem by design.</b> Every other entry point here can
    /// recover from a script the server has forgotten, because it holds the body and can re-load it. A
    /// caller who has only a hash cannot, so a <c>NOSCRIPT</c> error surfaces rather than being repaired -
    /// which is exactly what <c>IDatabase.ScriptEvaluate(byte[] hash, ...)</c> has always done.
    /// </remarks>
    public static ValueTask<RespResult> EvaluateHashAsync(
        this RespScripts scripts,
        scoped ReadOnlySpan<byte> hash,
        ReadOnlySpan<RedisKey> keys = default,
        ReadOnlySpan<RedisValue> args = default,
        bool readOnly = false,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => EvaluateHash(scripts, hash, keys, args, readOnly, flags, RespHandlers.Result, cancellationToken);

    private static ValueTask<TResult> EvaluateHash<TResult>(
        RespScripts scripts,
        scoped ReadOnlySpan<byte> hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool readOnly,
        CommandFlags flags,
        IRespHandler<TResult> handler,
        CancellationToken cancellationToken)
    {
        if (hash.IsEmpty) throw new ArgumentException("A script hash is required.", nameof(hash));

        var command = readOnly
            ? ForReadOnlyScript(scripts.Context.CommandMap, RedisCommand.EVALSHA_RO, ref flags)
            : RedisCommand.EVALSHA;
        var cmd = scripts.Context.Render($"{command}{hash}{(RedisValue)keys.Length}{keys}{args}");
        return scripts.Context.SendAsync(ref cmd, flags, handler, cancellationToken);
    }

    /// <summary>SCRIPT LOAD: put a script in the server's cache and learn its SHA1.</summary>
    /// <param name="scripts">The script command group.</param>
    /// <param name="script">The Lua source.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <returns>The SHA1 as lower-case hex, exactly as the server reported it.</returns>
    /// <remarks>
    /// Hex rather than bytes, because that is what every consumer of the answer keys on - the endpoint's
    /// record of loaded scripts and <c>EVALSHA</c> both take the text. <c>IServer.ScriptLoad</c> converts.
    /// Retried as a connection-level command: the SHA is a pure function of the script, so a replay on any
    /// node returns the same answer.
    /// </remarks>
    internal static ValueTask<string?> LoadHex(
        this RespScripts scripts,
        string script,
        CommandFlags flags = CommandFlags.None,
        CancellationToken cancellationToken = default)
        => scripts.Context.SendAsync(
            $"{RedisCommand.SCRIPT}{RespLiterals.Load}{script.AsRedisValue()}",
            flags.WithRetryCategory(CommandFlags.CommandRetryConnection),
            RespHandlers.String,
            cancellationToken);

    /// <summary>The 20 bytes of a SHA1 given as 40 hex characters, or null when it is not one.</summary>
    /// <param name="hex">The hex text.</param>
    internal static byte[]? Sha1Bytes(string? hex)
    {
        if (hex is not { Length: 40 }) return null;
        var bytes = new byte[20];
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = HexValue(hex[2 * i]), lo = HexValue(hex[(2 * i) + 1]);
            if ((hi | lo) < 0) return null;
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;

        static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    /// <summary>The shipped <see cref="RedisResult"/> shape, for <c>IDatabase.ScriptEvaluate</c>.</summary>
    /// <remarks>
    /// <b>Permanent, not scaffolding</b>, and internal for the same reason the <c>*Array</c> shims are:
    /// <see cref="RedisResult"/> materialises the whole reply, which is what the low-allocation
    /// <see cref="RespResult"/> exists to avoid, so new code must not be able to pick it by accident.
    /// It is the same parse either way - the handler reuses <c>RedisResult.TryCreate</c> - so the two
    /// shapes cannot drift.
    /// </remarks>
    internal static ValueTask<RedisResult> EvaluateResult(
        this RespScripts scripts,
        string script,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool readOnly,
        CommandFlags flags)
        => Evaluate(scripts, script, keys, args, flags, readOnly, RedisResultHandler.Instance, default);

    /// <inheritdoc cref="EvaluateResult"/>
    internal static ValueTask<RedisResult> EvaluateHashResult(
        this RespScripts scripts,
        scoped ReadOnlySpan<byte> hash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool readOnly,
        CommandFlags flags)
        => EvaluateHash(scripts, hash, keys, args, readOnly, flags, RedisResultHandler.Instance, default);

    /// <summary>
    /// EVAL or EVALSHA exactly as the shipped <c>IDatabase.ScriptEvaluate</c> sends it: one command, no
    /// preamble, no caching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <see cref="EvaluateAsync"/>, and the difference is user-visible.</b> The shipped
    /// string overload chooses <c>EVALSHA</c> when the string looks like a SHA1 and otherwise sends
    /// <c>EVAL</c> with the body - every call, uncached. The context surface instead does <c>SCRIPT LOAD</c>
    /// then <c>EVALSHA</c>, which is better but is not the same thing on the wire: it adds a load to the
    /// first call and leaves the script in the server's non-evictable cache.
    /// </para>
    /// <para>
    /// Changing that for existing callers is a decision rather than a translation, so the adapter keeps
    /// what they have. New code gets the caching path by calling <see cref="EvaluateAsync"/> directly.
    /// </para>
    /// </remarks>
    internal static ValueTask<RedisResult> EvaluateDirectResult(
        this RespScripts scripts,
        RedisValue scriptOrHash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool isHash,
        bool readOnly,
        CommandFlags flags)
        => EvaluateDirect(scripts, scriptOrHash, keys, args, isHash, readOnly, flags, RedisResultHandler.Instance);

    /// <inheritdoc cref="EvaluateDirectResult"/>
    /// <remarks>
    /// The <see cref="RespResult"/> twin, for <c>IDatabase.ScriptEvaluateResp</c> - which is the shipped
    /// surface's own low-allocation shape and so wants the same uncached send, not a different one.
    /// </remarks>
    internal static ValueTask<RespResult> EvaluateDirectResp(
        this RespScripts scripts,
        RedisValue scriptOrHash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool isHash,
        bool readOnly,
        CommandFlags flags)
        => EvaluateDirect(scripts, scriptOrHash, keys, args, isHash, readOnly, flags, RespHandlers.Result);

    private static ValueTask<TResult> EvaluateDirect<TResult>(
        RespScripts scripts,
        RedisValue scriptOrHash,
        ReadOnlySpan<RedisKey> keys,
        ReadOnlySpan<RedisValue> args,
        bool isHash,
        bool readOnly,
        CommandFlags flags,
        IRespHandler<TResult> handler)
    {
        var command = isHash
            ? (readOnly ? RedisCommand.EVALSHA_RO : RedisCommand.EVALSHA)
            : (readOnly ? RedisCommand.EVAL_RO : RedisCommand.EVAL);

        var cmd = scripts.Context.Render($"{command}{scriptOrHash}{(RedisValue)keys.Length}{keys}{args}");
        return scripts.Context.SendAsync(ref cmd, flags, handler);
    }

    /// <summary>
    /// Pick the command to identify a read-only script request by, honouring the command map. The
    /// server-version half of the decision cannot happen here - we do not know yet which server this
    /// will go to - so that is resolved at write time; see <c>CanUseReadOnlyScripts</c>.
    /// </summary>
    /// <remarks>
    /// When we fall back, the retry category is pinned to the read-only one first. EVAL_RO defaults to
    /// CommandRetryReadOnly and EVAL to CommandRetryWriteAccumulating, so simply swapping the command
    /// would quietly make a script the caller asked for read-only retry like a write. Falling back is
    /// about what the server will accept, not about what the caller asked for.
    /// </remarks>
    internal static RedisCommand ForReadOnlyScript(CommandMap map, RedisCommand readOnlyCommand, ref CommandFlags flags)
        {
        // both, for the same reason CanUseReadOnlyScripts wants both: hash-vs-script is decided later
        if (map.IsAvailable(RedisCommand.EVAL_RO) && map.IsAvailable(RedisCommand.EVALSHA_RO))
        {
            return readOnlyCommand;
    }

        flags = flags.WithRetryCategory(CommandFlags.CommandRetryReadOnly);
        return readOnlyCommand == RedisCommand.EVALSHA_RO ? RedisCommand.EVALSHA : RedisCommand.EVAL;
    }

    /// <summary>SCRIPT FLUSH: discard every script this server has cached.</summary>
    /// <param name="scripts">The scripting command group.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>
    /// <b>Server-scoped, unlike the rest of this group.</b> A script cache belongs to one node, so this
    /// empties the node that was asked and nothing else - which is also why <c>IServer</c> is where it is
    /// exposed, and why it is guarded by admin mode there.
    /// </remarks>
    public static ValueTask FlushAsync(this RespScripts scripts, CommandFlags flags = CommandFlags.None, CancellationToken cancellationToken = default)
        => scripts.Context.SendAsync($"{RedisCommand.SCRIPT}{RespLiterals.Flush}", flags, cancellationToken: cancellationToken);

    /// <summary>SCRIPT EXISTS: whether this server already holds a script, by its hash.</summary>
    /// <param name="scripts">The scripting command group.</param>
    /// <param name="hash">The script's SHA1, hex-encoded.</param>
    /// <param name="flags">Command flags.</param>
    /// <param name="cancellationToken">Cancels the request: one not yet written is never sent; one already written still runs on the server, and its reply is discarded.</param>
    /// <remarks>
    /// <b>One hash, one answer.</b> The command is variadic and replies with one flag per hash asked
    /// about, but <c>IServer</c> asks about exactly one - so this does too, rather than handing back a
    /// one-element array for the caller to unwrap.
    /// </remarks>
    public static ValueTask<bool> ExistsAsync(this RespScripts scripts, RedisValue hash, CommandFlags flags = CommandFlags.None, CancellationToken cancellationToken = default)
        => scripts.Context.SendAsync(
            $"{RedisCommand.SCRIPT}{RespLiterals.Exists}{hash}",
            flags.WithRetryCategory(RespServerRetry.NodeLocalRead),
            ScriptExistsHandler.Instance,
            cancellationToken);

    /// <summary>Reads <c>SCRIPT EXISTS</c>: one flag per hash asked about, and we asked about one.</summary>
    internal sealed class ScriptExistsHandler : IRespHandler<bool>
    {
        internal static readonly ScriptExistsHandler Instance = new();

        public bool Parse(ref RespReader reader)
        {
            // a scalar is tolerated as well as the array, exactly as the shipped processor does: the
            // reply shape is the server's to choose and both have been seen
            if (reader.IsNull) return false;
            if (reader.IsScalar) return reader.ReadBoolean();

            if (reader.IsAggregate && reader.TryMoveNext() && reader.IsScalar)
            {
                var exists = reader.ReadBoolean();
                if (!reader.TryMoveNext()) return exists;
            }

            throw new RespException("Unexpected SCRIPT EXISTS reply.");
        }
    }
}
