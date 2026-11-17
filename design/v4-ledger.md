# v4 ledger

The current list of what is pending on `v4` and what is waiting on a decision. **Keep it short:** when an
item closes, delete it here and let the commit message hold the story. History and reasoning live in
`v4-alpha-plan.md` (how the old core was retired) and `message-core-replacement.md` (why); closed ledger
entries live in this file's git history. Behaviour differences v3 → v4 that belong in release notes are
catalogued in `notes/v3-v4-behaviour-audit.md`.

Last updated: 2026-11-17.

## Decisions waiting on Marc

Each has a default the work proceeds on until answered.

| # | question | default meanwhile |
|---|---|---|
| D3 | **Alpha cadence.** Latest is 4.0.165-alpha, cut 2026-11-13 (GitHub Release on `v4` runs `release.yml`). | cut when Marc says; CI green first |
| D4 | **Post the v4-port findings to PR #3251?** Drafted (spin-before-park +2.6% on `incr-conc64` in #3251's own reader; read-size hint is a hypothesis; drain-per-wake does not pay). Outward-facing. | not posted |

Closed since the last update: **D5** — SER014 batch/transaction shipped as-is (2ca83a9d) and has not needed
revisiting: conditions stay on the shipped `Condition` type, `RespBatch`/`RespTransaction` stay structs.

## Pending work (no decision needed)

- **Windows CI is red at the branch head: `ExceptionFactoryTests.ServerTakesPrecendenceOverSnapshot` (RESP2)**
  failed on both `v4` push runs since the 3.4.31 merge (c76d4e6b and 0d18c571, both 2026-11-13): expected
  "The connection to 127.0.0.1:6379 was closed", actual "Aborting (AllowConnect: False)" out of
  `RespEndpointExecutor.ConnectAsync` via `RespConnectionManager.DialEndpointSoon`. 2 of 2 on push where the
  same commits' PR runs were green, so it may be ordering/environment — but it is deterministic enough to
  diagnose before the next alpha. (The 0d18c571 run also hit two known flakes, listed under Watch.) Ubuntu
  is green on both runs.
- **Absorb `main` by merging**, little and often; resolve each conflict by re-expressing the change in v4
  terms and say how in the merge message. **The drift-check command is now misleading:** #3298 and #3355 were
  intent-ports squash-merged onto v4, so ancestry is not joined and `git rev-list --count v4..origin/main`
  overstates (it answers 15 today). Measure drift from the newest `main` commit the last port covered
  (#3355 covered through #3352). True drift right now: **one commit** — ca51eb6a (#3333, prefer the announce
  hostname when dialing a cluster node on reconnect, 2026-11-16), not yet ported. The final landing on
  `main` must be a real merge, never a squash.
- **Cache performance — ideas still to explore** (the design as built, with reasons and rejections:
  `design/client-side-cache.md`; tools: `CacheHitSendBenchmarks` with `--inProcess`, `cache-scaling`,
  `design/admitsim.py` — a model of doorkeeper + slabs + budget that imports RespFest's `harness.keyseq`).
  Roughly in order:
  - [ ] The gap to Lettuce's 79% hit rate (check which cache its runner uses; Caffeine's W-TinyLFU compares
    the candidate's frequency with the victim's). Model before code: second-chance eviction and
    frequency-vs-victim admission in `admitsim.py`, on standard traces (ARC/LIRS, Caffeine's simulator) as
    well as Zipf — not fitted to one benchmark. Current default measures 75.9–76.1%; under ~1 point: close.
  - [ ] Second chance at slab eviction: a write-once "touched" flag per entry; touched entries copied forward
    rather than evicted with their slab. Only if the Zipf workload shows hot keys churning out.
  - [ ] The cached miss sends untyped and parses afterwards, where an uncached send parses typed on the reader.
  - [ ] `RespMultiplexerExecutor.CanWritePreamble` still resolves a route (server selection + the endpoint
    lock) on every paired send; the executor is bound to one database, so it is probably fixed per instance.
    Check, then compute it once.
  - [ ] `RespKeyTable` nodes: check whether a key's node outlives every entry that depended on it (growth
    with the keyspace, not the cache).
  - [ ] Slab recycling via per-thread reader counters — only if large-object churn (gen2) shows up as a cost.
  - [ ] The interpolated builder: ~26ns over a 4ns floor, plus 10–15ns on every group method from
    inline-budget exhaustion (6548bffe). Draft parked as `design/parked/builder-inlining.patch`
    (`git apply` it); unmeasured.

  Rejected, with reasons in the commits: per-entry striped refcounts (~150 MB of padded counters),
  pinned-object heap for entries (no better), finalizer-driven slab recycling (interior pointers outlive
  the wrapper; finalizable objects are mid-life by construction).
- **CancellationToken dropped before `SendAsync`:** geo `SearchCore` passes `default` where the caller's
  token belongs. #3356 found and fixed the same pattern in `RangeByScoreCore` and `CombineCore`; sweep the
  remaining internal cores for it.
- **The core has no `Proxy.Twemproxy` handling** — stated in the code as "a real gap". Decide: port it, or
  4.0 ships without proxy support and the option refuses loudly.
- **Test-coverage debts:** nine regression pins (born from production incidents) that died with the old core
  and were not re-pinned end-to-end — #2919 teardown misroute, #3282 mid-batch fault injection, cluster
  tie-breaker probe suppression, `RequestBufferPool` accounting over live traffic, and five more. Behaviour
  is believed fine; each is a candidate ticket. The list with detail: `notes/v3-v4-behaviour-audit.md`.
- **Two-core leftovers:**
  - `Subscription._subscribed` / `IsSubscribed` / `AnySubscribed` now mean only "something subscribed
    this"; collapsing them changes the never-sent case — look, don't delete.
  - ~24 `ConnectionsIfCreated?.` null-tolerances; `ServerEndPoint.IsConnecting => false`.
  - Topology flows both ways between `RespTopology` and `ServerSelectionStrategy` ("phase D").
- **Subscription re-aim waits for its in-flight send to fail** (instant on Linux, ~2s on Windows, a connect
  timeout for a dropped SYN). Fixing it means de-duplicating a subscription that lands on two nodes — the
  problem PR #3353 addresses on `main` (overlapping subscription restores cannot register on two servers);
  when that lands, port the approach rather than inventing a second one. Not urgent.
- **An abandoned connect (timeout) keeps its socket in SYN-SENT until the OS gives up** — default mode calls
  `ConnectAsync` without the token (5 blackholed attempts left 5 sockets; dedicated mode, which connects
  synchronously with cancellation disposing the socket, leaves none).
- **Prose still says "the new core".** The rename pass (a1673a96) renamed types and members only; ~35 files
  across `src/` and `docs/` still say "the new core" / "old core" where there is now one. A wording pass to
  "the connection manager" (or just "the core") — noisy, so its own commit.
- **`MaintenanceOptInServerTests.SequenceIdsAdvanceAndCanBeRepeated` fails when its class runs alone** (5 of
  5; usually passes in the full suite): `SendShardNotification` reaches 0 connections where 1 opted in — the
  opt-in has not landed at the fake server when the notification is sent. A test-ordering dependence; look
  at what `OptInAsync` awaits.
- **`CancellationTests.WithCancellation_CancelledToken_ThrowsOperationCanceledException`** — a race in the
  test itself: `Task.WaitAsync(token)` returns an already-completed task as-is, and with inline sends a
  loopback SET can complete before `WaitAsync` looks. It should cancel through the API's own token.

### Watch (flakes, not yet reproducible)

- **"Nothing inbound" timeouts in `RespCacheChurnTests`** — 2026-10-09, 2026-10-10 (Ubuntu) and 2026-10-12
  (Windows net10.0), purely async tests, clean locally over 25 class runs + 5 full suites. If it recurs,
  take a dump mid-stall (the 2026-10-09 inline-completion stall was found that way) rather than retrying.
- **`BacklogTests.TotalOutstandingIncludesBacklogQueue`** — intermittent and pre-existing (bisect put an
  unrelated commit at the same failure rate as the head). First suspect: the pings completing or faulting
  before the counters are read.
- **`QueuedResultTests.RetryTransactionFireAndForgetSharesOneCompletedTask`** — 2026-10-08 (net8.0, full
  suite) and again 2026-11-13 (Windows push run of 0d18c571, RESP2, `Assert.True` failure). It asserts the
  per-command tasks are settled when `ExecuteAsync` returns, which the retrying transaction executor
  guarantees by awaiting them; two sightings now, so worth a look at what the assertion saw.
- **net8.0 cluster tests timing out at 10s under the full suite** (`DisabledCommandsStillConnectCluster`
  and friends). Seen 2 of 7 full runs after the per-thread operation cache (03bbb05a), 0 of 3 before;
  0 of 16 alone. If it recurs, compare with and without that commit first.
- **Known flakes:** `SentinelTests.AbortOnConnectFailFalseRecoversOnceSentinelsAppear` (Ubuntu),
  `TouchIdleTime` (6381), `RespAggregateTiming`, `ADeferredWalkNeedsNoStorageAtAll` (net8, full suite only),
  `ADeferredWalkAgainstMaterialiseThenRead` (again 2026-11-13, Windows), `SlotLessNodesAreKnownButNotConnected`
  (net8 full suite). Pub/sub under full-suite load: `PubSubRouted` and
  `KeyNotification_CanObserveSimple_ViaQueue` have each failed in two full runs (RESP2 and RESP3), always
  clean in isolation — the leading suspect if a pattern holds.
- Dropped from Watch: `GetServerByKeyMemoization` — not seen since the speculative fix d45eadf7
  (2026-10-09), across every CI run since.

## Backlog (after the alpha)

- **Run `ActiveActiveCacheScenarioTests`** against a real Active-Active deployment: written and compiled,
  never executed (needs the fault-injector environment). It also asserts what the in-process servers cannot —
  that an inactive member's cache hears a write replicated from elsewhere.
- **Alternative client-side-cache invalidation sources.** The opt-in/out story is now largely settled:
  per-multiplexer via `ConfigurationOptions.ClientCache` (null by default), per-command via the serve/store
  `CommandFlags` and `CachePolicy` (#3354, which also added `CachePolicy.Storage = External` for layered
  caches such as HybridCache). What remains: invalidation sources for deployments without usable
  `CLIENT TRACKING` (proxies, older servers, some managed offerings) — **keyspace notifications**
  (`__keyspace@<db>__:*`; needs `notify-keyspace-events` server-side, no RESP3 requirement) and **explicit
  pub/sub** (an application-defined channel the writer publishes to) — and the policy when the chosen
  source is unavailable: refuse loudly, as RESP2 + cache does today, versus degrade to no caching.
- **Client-side cache: public counters / metrics** — hits, misses, stored, refused (admission),
  invalidations, stale-served. They exist internally (`RespClientCache.Stored`, `Count`, …; tests read
  them) but nothing public: pick a shape (`System.Diagnostics.Metrics` instruments, a snapshot struct, or
  both).
- **Operation pool size at batch depth, then buffer packing.** The 2026-10-07 batch profile (SADD, 50
  callers × 100 per batch): v4 is round-trip bound, command assembly is 0.9–1.5% of samples — so packing at
  construction cannot pay — and the real lever is the operation pool's 128 slots against thousands in
  flight, which makes most operations (266 B + request bytes) allocate fresh. A larger or depth-adaptive
  pool first (watch the 8-probe `TryTake`); then batch/transaction **buffer packing** — one shared buffer,
  contiguous run to the transport. The abandoned RESPite spike implemented packing fully (PR #2959,
  `marc/respite`: `RespBatch`, `{Buffering,Merging}BatchConnection`, `SynchronizedBlockBufferSerializer`).
  Check first whether the interpolated writer's reserved `*N` prologue still lets frames sit adjacently,
  or leaves gaps that cost a copy.
- **Unobserved faults on the new surface:** a non-pooled `async ValueTask` that completes asynchronously is
  `Task`-backed (every one down-level, 10 of 13 on net6+), so a faulted one the caller drops can still
  raise `UnobservedTaskException`. Dropping an un-awaited `ValueTask` is already misuse; low priority.
- **Performance, remaining ideas** (each promises single-digit percent, so parked): `RespPayload` as a
  struct (public API, ~32 B/op); small request bytes inline in the operation; `IDatabase`'s remaining
  ~100 B/op over the context API is the `Task` plus the pool work item that runs its completion — the work
  item could go if the operation queued itself (`IThreadPoolWorkItem`, net6+); slim the operation further
  (266 B: token + registration are 24, request memory/owner/refcount 28); a growable SPSC ring for the
  pending queue (risky, three threads touch it). Not worth it: stackalloc request rendering, delaying the
  writer to coalesce, a dedicated small-buffer pool. **Warm-up** is JIT tiering, as for v3: left to
  applications, and benchmarks should warm up before measuring.
- **Trusted-callback completion:** inline completion measured and ruled out as a default (head-of-line
  blocking pins throughput to the serial 1/N ceiling — RespFest work league, `results/work-league-20261007`,
  against Respire's `CompletionScheduler`). The one variant left: batching pool wakes per parse pass with
  continuations kept asynchronous.
- **Non-nullable `AppendFormatted(long)` / `(double)` overloads** on `RespRequestBuilder`, beside today's
  `long?`/`double?` (asked for by the NRedisStack port). Needs an overload-resolution sweep across the
  interpolated call sites before adding.
- **`IServer` overload audit:** the overload-minimalism audit (`notes/api-surface/overload-audit.md`, acted
  on in #3356) deliberately did not examine `IServer` — large, node-scoped, mostly one method per command,
  but unexamined.
- **.NET 11 TFM:** try runtime async; prototype caller-driven TLS (`TlsBufferSession`); run the suite and
  RespFest on 11 early. **The RC2 thread-pool regression (dotnet/runtime#135855) lands mostly on our own
  hops** (2026-10-15, 64 callers, GET 1 KiB): v4 default 762k → 532k ops/s (−30%) at 5µs caller work, but
  `SEREDIS_DEDICATEDTHREADS=1` on RC2 gives 758k (.NET 10 dedicated: 798k). The Redis-free repro splits its
  −36% about 1/3 IO hop, 2/3 caller continuations. Next: dedicated reader (+writer) by default on net11+,
  with a cap for wide clusters; 1 caller costs −7% ops but less CPU per op.

## Status

Newest first; closed entries are deleted (this file's git history holds them).

- **2026-11-17: ledger refresh.** Closed since 2026-10-13 and removed above, the headline being the
  **method-replaying decorators are gone** (#3338 + the tx-retry work): `KeyPrefixed*` (~4,100 lines of
  member-by-member tests with them), `RetryDatabase`, `RetryTransaction`, the `[AutoDatabase]` generator and
  `CapturedArgs` are deleted; `WithKeyPrefix`/`WithRetry` re-context onto the executor chain, retrying
  transactions replay frames per attempt (`RespRetryTransactionExecutor`) and now carry `asyncState`, and
  `MultiGroupDatabase` is a slim `RedisDatabase` over `RespGroupExecutor`. The stale `RetryDatabase`/
  `RespRetryExecutor` comments went with the files.
- **November, shipped in order:** #3349 cache hits reuse the deserialized result for provably-shareable
  types (with #3350's per-key removal notifications folded into its branch) — the HybridCache enablers;
  #3338 decorators onto the context/executor core; #3354 per-command cache serve/store flags and
  `CachePolicy.Storage = External`; #3355 intent-merge of `main` to 3.4.31 (MOVING drain hold, CLUSTER
  SLOT-STATS, keyed routing tests, SER1xx experiment block); #3356 overload minimalism (`SortedSetRange`,
  `Expiration` on RESTORE, `GeoCenter`; audit and outcome in `notes/api-surface/overload-audit.md`).
- **2026-11-03: independent v3 → v4 behaviour audit** for release notes: `notes/v3-v4-behaviour-audit.md`.
  Empirical headline: both branches' full suites, same server, fail on exactly the same tests; v4 runs
  ~1,270 more, all new coverage. Its "known gaps" and "test-coverage debts" are folded into Pending above.
- **2026-10-30: ASK re-sends drop the `CLIENT CACHING` prefix and `ASKING` is exempt from the sequence
  check** (4744cfc7) — the two one-shot server flags cannot be ordered so both survive; the redirect wins,
  the cache refuses the prefix-dropped reply (`RefusedUntracked`). `design/command-flow.md` has the detail.
- **2026-10-29: high-integrity mode restored** (`ConfigurationOptions.HighIntegrity`, lost with the old
  core): `ECHO <counter>` behind every checked command at the `Stage` choke point, reply held until the
  check validates, `ResponseIntegrityFailure` on mismatch. After the look-ahead + staged-write mitigation
  (9106f4ce, 91810686): allocation per op equal to mode-off, RSS at baseline; cost of the mode is −5%
  sequential, −18…−26% at high concurrency — the rest is the mode itself (twice the frames each way).
  Mechanism and tests: `design/command-flow.md`, `RespSequenceCheckTests`, `HighIntegrityWireTests`.
- `v4` branched 2026-10-06 from `marc/v4-core-operation`; packages compute as `4.0.N-alpha` (latest
  4.0.165-alpha, 2026-11-13), assembly version `4.0.0.0`. `main` drift: 1 commit (#3333; see Pending —
  the rev-list count overstates since the squashed intent-ports). CI at the branch head 0d18c571: Ubuntu
  green; Windows red on `ServerTakesPrecendenceOverSnapshot` (see Pending). The #3356 PR runs were fully
  green on both platforms.
