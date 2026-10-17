# C#: Kestrel + Npgsql

**Front: direct.** Kestrel listens on `HOST:PORT` itself. Behind nginx it works unchanged on 127.0.0.1:3000,
but a proxy hop costs CPU on the same cores, and Kestrel already holds tens of thousands of keep-alive
connections.

Implements [`SPEC.md`](../../SPEC.md). Passes `test/run.sh`: 197/197 on the dev seed and on the full seed.

| Piece | Version |
|---|---|
| .NET | SDK 10.0.402, runtime 10.0.13 (pinned in `global.json` and `install.sh`, sha512 checked) |
| HTTP | ASP.NET Core Kestrel (shared framework), `CreateEmptyBuilder` + `UseKestrelCore`, own router |
| JSON | `System.Text.Json`: `JsonDocument` to read, `Utf8JsonWriter` to write |
| Postgres | Npgsql 10.0.3 (the only package; `packages.lock.json`, restored with `--locked-mode`) |
| JWT | own HS256 (`HMACSHA256`, `Base64Url`) |
| bcrypt | own, cost 10, `$2a$` (verifies `$2a$`, `$2b$`, `$2y$`) |

## Run

```bash
sudo bash install.sh     # .NET SDK into /usr/local/dotnet
bash build.sh            # -> bin/conduit.dll
DATABASE_URL=postgres://conduit:conduit@127.0.0.1:5432/conduit JWT_SECRET=dev-secret PORT=3000 bash start.sh
```

Environment: `DATABASE_URL`, `JWT_SECRET` (required), `HOST` (127.0.0.1), `PORT` (3000), `DB_POOL_SIZE` (read
loops, each with a connection; 2 per core), `DB_WRITERS` (2), `BCRYPT_THREADS` (one per two cores, at least 1),
`BCRYPT_BATCH_MS` (200), `STATS_SECONDS` (10; 0 turns the stats line off), `DOTNET_GCgen0size` (64 MB, set in
`start.sh`).

## Layout

| Path | Namespace | What |
|---|---|---|
| `Program.cs`, `Router.cs` | `Conduit` | settings from the environment, Kestrel, the route switch |
| `Http/` | `Conduit.Http` | request bodies, query strings, pooled JSON responses |
| `Endpoints/` | `Conduit.Endpoints` | the handlers: users, profiles, articles, comments, tags |
| `Data/` | `Conduit.Data` | Npgsql data sources, the SQL, the read groups and the group commit writer |
| `Security/` | `Conduit.Security` | JWT, the `Authorization` header, bcrypt and its threads |
| `Tests/`, `Benchmarks/` | | their own projects; `Tests/` mirrors the folders above |

## Results

On a box like the scoring one: the server and Postgres 18 on an 8-vCPU Google Cloud `c4-highcpu-8` (Xeon
Platinum 8581C, 16 GB, `box/setup-box.sh` unchanged), the load generator on a separate 8-vCPU machine, the full
seed, think-scale 1 (the workload's real 7 s). Each run copies a fresh database, restarts Postgres, warms up at
500 users for 3 minutes, ramps for 60 s and measures 60 s. Pass is p95 < 500 ms, p99 < 1 s, errors < 1%.
Memory is the box's peak.

| Users | This version | b2a554c (first version of this PR) | Go example |
|---|---|---|---|
| 20,000 | | | pass: p95 3 ms, p99 10 ms, 54% CPU |
| 30,000 | | | pass: p95 8 ms, p99 15 ms, 84% CPU |
| 60,000 | pass: p95 23 ms, p99 89 ms, 73% CPU, 3.4 GB | | |
| 80,000 | pass: p95 32 ms, p99 105 ms, 88% CPU, 3.9 GB | pass: p95 59 ms, p99 149 ms, 96% CPU, 4.2 GB | |
| 100,000 | **pass: p95 273 ms, p99 313 ms**, 98% CPU, 4.1 GB, 25,280 req/s | fail: p95 1,198 ms, p99 1,880 ms, 5.4 GB | |

No errors in any run. 100,000 users is the most this rig can drive: one load machine opens about 64k connections
per target address, and the target has two. At 100,000 the box is at 98% CPU, 70 points of it Postgres, so the
limit is CPU, not memory (4.1 of 16 GB). The Rust example didn't build on this rig (its toolchain wasn't installed
for the build user), so it is only in the table below.

What took 100,000 from fail to pass was the read groups (one Sync per batch of reads instead of Npgsql's
multiplexing): measured against the same code with multiplexing, p95 at 80,000 went from 83 to 32 ms, and at
100,000 from 763 ms (fail) to 273 ms. Before that, text columns written from their UTF-8 bytes halved the gen0
collections, and turning Npgsql's SQL rewriting off saved parsing every parameterless statement.

Tried and not kept, on the same rig: workstation GC (p95 130 ms at 80,000) and a 256 MB gen0 budget (p95 231 ms,
p99 577 ms at 80,000); commands pooled per connection, which cut the bytes per read by 80% in the micro-benchmark
but at 100,000 gave p95 376 ms, p99 466 ms and 4.9 GB.

Earlier, on smaller boxes, the most users each server held with users thinking a tenth of the 7 s
(`-think-scale 0.1`), so ranks only, not the scoring box's counts. Go and Rust are
[`examples/go`](../../examples/go) and [`examples/rust`](../../examples/rust) unchanged, on the same Postgres.

| CPUs | C# | Go | Rust | C# req/s at its limit |
|---|---|---|---|---|
| 1 (x86-64 VPS) | **1,250** | 700 | 500 | 2,160 |
| 2 (arm64) | **5,500** | 2,500 | 2,500 | 11,150 |
| 4 (arm64) | **13,000** | 5,000 | 5,000 | 20,730 |
| 6 (arm64) | **12,000** | 6,000 | 6,000 | 22,460 |

The 1-CPU box is a 2-vCPU OVH VPS; 2 to 6 CPUs are a Mac mini M1 in a Linux VM, where CPUs 5 and 6 are efficiency
cores and the load generator had two cores left, so the 6-CPU row understates every server.

Where it comes from, at 3,500 users on 4 CPUs (CPU time per request):

| | Server | Postgres | Total |
|---|---|---|---|
| C# | 0.15 ms | 0.14 ms | 0.29 ms |
| Go | 0.17 ms | 0.26 ms | 0.43 ms |
| Rust | 0.18 ms | 0.29 ms | 0.47 ms |

Postgres is the larger half of every request, so most of the work went into the SQL and the connections, then
into bcrypt, the most expensive single request by far.

### SQL

Every response is byte-identical to the reference (the conformance transcript); the statements are different:

- **One statement per request.** Follow, unfollow, favorite, unfavorite, update and delete check, write and
  read back in one statement with CTEs. Article lists send the page and its count as one `NpgsqlBatch`
  (one round trip, one implicit transaction). Create sends insert, tags, links and read-back together.
- **Favorite counts under concurrent toggles.** The read half of a favorite toggle sees the statement's snapshot,
  which misses a favorite another transaction committed while the insert waited for it (`ON CONFLICT` and
  `DELETE` look past the snapshot). After the statement the viewer's favorite exists (or not, for unfavorite)
  whoever wrote it, so the count is the snapshot's, corrected by whether the snapshot holds that row. Counting
  the rows the write changed instead returned 0 for one favorite, 1 after it was removed, and counted a
  favorite twice when it replaced one removed meanwhile; the tests replay each race with a lock held by a
  second connection.
- **Page first, then decorate.** Lists pick the page's ids with an index-only scan, then join and compute
  tags, favorite counts and flags for those rows only, not for the `OFFSET` rows before them.
- **A constant bound on the page.** With `LIMIT $2` the generic plan expects a tenth of the table and puts a
  Memoize on the users join, which zeroes a hash table that size on every execution. The ids are taken with
  `LIMIT 100` (the largest limit allowed) and `$2` applies outside it, so the plan expects at most 100 rows.
- **The feed** takes the newest `limit + offset` articles per followee (a `LATERAL` index scan each) instead
  of joining every article of every followee.
- **Tag names by primary key** instead of a hash join over all tags for each article.
- **Tag lists as JSON.** Postgres returns each article's `tagList` (and `/api/tags`) as JSON text, which goes
  into the response as is: cheaper on both sides than a `text[]` column the server decodes and re-encodes. The
  feed's CPU per request fell 11%, the other lists' a little (epbench, 2 rounds). Non-ASCII tags are sent as
  UTF-8 rather than `\u` escapes; the JSON is the same.
- **An anonymous viewer** (`$1 = 0`) skips the favorited and following probes.
- **Generic plans.** `plan_cache_mode=force_generic_plan`, `jit=off` and `max_parallel_workers_per_gather=0`
  per session. With `LIMIT $2 OFFSET $3` as parameters Postgres keeps re-planning every execution; these
  statements are written so the generic plan is the right one, so each connection plans each statement once
  (Npgsql auto-prepares after 2 uses).

### Connections

- **Read groups.** Every read goes to a queue. Read loops (two per core), each on its own connection, send what
  has queued (up to 32 reads) as one `NpgsqlBatch` with one Sync. Postgres then starts and ends one implicit
  transaction and flushes its replies once for the group, instead of once per statement as with Npgsql's
  multiplexing; Read Committed still takes a fresh snapshot for every statement. On a quiet 2-vCPU x86 VPS the Sync
  per statement cost 31 of a read's 180 µs of Postgres CPU. A read completes once its statement has ended (a
  statement can still fail after the rows its read took); a failing statement fails only its own read, and the reads after it (which Postgres skipped) run again as the next group.
  Against multiplexing on that VPS at saturation (median of 4 and 3 rounds): 2,766 vs 2,600 requests per second,
  p95 154 vs 201 ms, p99 179 vs 257 ms, CPU per request 0.359 vs 0.383 ms; at three quarters of the load, the same.
  As with multiplexing, Postgres runs a few busy backends instead of one per request.
- **Group commit.** Every write goes to a queue. Two writer loops, each on its own connection, send what has
  queued (up to 128 writes) as one `NpgsqlBatch`: one round trip and one transaction, so one WAL flush commits
  all of them. Each write's response is built from its row inside the batch and sent only after the commit
  (rule 6). If a statement fails, the transaction rolls back; the writes before it commit together, it fails
  with its own error (a duplicate email fails only its own sign-up, 422), and the writes after it are tried as
  the next group. The result is that of running them one by one in order, and a write runs at most twice before
  it commits (three times if the writes before the failing one then fail together and each runs alone). A write
  that loses a deadlock to the other loop's group runs again. A write whose client has gone is dropped if no
  group has taken it yet. A broken connection fails the writes it held, since whether they committed is
  unknown, and the loop reconnects. A write waits at most 2 s (`lock_timeout`) for a row lock someone else holds:
  then it alone fails (503), instead of holding its whole group for the 30 s statement timeout. At most 4,096
  writes wait for a loop; more are refused at once (503 with `Retry-After`), so a stalled database can't pile up
  requests and their bodies. Under the workload the writes in flight stay in the tens. On 4 CPUs at 12,000 users:
  19,100 to 22,500 requests per second, app CPU per request 0.073 to 0.053 ms, Postgres 0.134 to 0.124 ms.
- **No reset on close, no enlistment, no TLS** on the loopback connection.
- **A request whose client has gone sends nothing.** Body parses take the request's abort token, and a queued read
  whose client has gone is dropped before a loop sends it. A read already sent runs to its end: cancelling it would
  take a Postgres cancel request, which would hit whatever statement of the group is running. At most 65,536 reads
  wait (near the knee every user can have one in flight); more are refused at once (503).

### bcrypt

A login or sign-up is about one request in a thousand, but a cost 10 hash is tens of milliseconds of CPU, as
much as a hundred or more other requests.

- **Dedicated threads.** Hashes run on their own threads, never on the thread pool, so a burst of logins
  queues behind them while every other request keeps its latency.
- **Up to four hashes interleaved.** Blowfish is a chain of dependent table lookups: each round waits for the
  load before it. A worker that finds more jobs waiting runs up to four hashes in one loop, so the CPU overlaps
  their chains. Per hash on the x86-64 VPS:

  | | ms per hash |
  |---|---|
  | BCrypt.Net-Next | 91.9 |
  | ours, one at a time | 76.0 |
  | ours, 2 interleaved | 43.1 |
  | ours, 3 interleaved | 31.6 |
  | ours, 4 interleaved | 31.0 |

  On arm64 four interleaved are 23.6 ms against 74.2 ms one at a time. Go's `x/crypto/bcrypt` is about 75 ms.
- **One thread per two cores.** Fewer threads mean jobs queue and get interleaved; but when every core is
  busy each thread only gets its share of the CPU, while logins grow with the cores. On 4 CPUs one thread let
  two thirds of the logins fail at 11,000 users; two kept every login working up to the limit. On 6 CPUs two
  threads lost 46% of the logins just past the limit, three kept them all. More is not better: at 12,000 users
  on 4 CPUs three threads served 3.5% fewer requests per second than two, four 5% fewer.
- **A batch window.** One worker at a time collects a batch: after the first job arrives it waits up to 200 ms
  for up to three more. At the workload's real pace (a session every 15 minutes per user, a quarter of them with
  a login or sign-up) 20,000 users make about 6 hashes a second, so without the wait nearly every hash would
  run alone at full cost. Logins are one request in a thousand, so the wait doesn't reach the p95. At 12,000
  users with a tenth of the think time, where hashes queue anyway, 0, 80 and 200 ms were within noise.
- **Abandoned logins are skipped.** A job whose client disconnected while it queued is dropped before it is
  hashed, so under overload the CPU goes to the logins still waiting. At most 256 jobs per worker wait (several
  seconds of hashing); more are refused at once (503).
- **Unknown emails cost the same** as a wrong password: the login checks against a hash of a random password,
  so response times don't tell which emails have accounts.

The implementation follows OpenBSD's and Go's. The tests check it against BCrypt.Net-Next with the same salt
for random passwords (property tests, every lane count), and against hashes made by Go. Passwords over
72 bytes are refused (422) instead of silently truncated; a malformed stored hash fails to verify instead of
throwing.

### Runtime

- **No spinning thread pool.** `System.Threading.ThreadPool.UnfairSemaphoreSpinLimit=0`: an idle pool thread
  sleeps at once instead of spinning for work, which burned CPU that Postgres, on the same cores, needed. The
  server's CPU per request fell from 0.258 ms to 0.194 ms at the same load (4 CPUs, median of 3 rounds).
- Server GC, non-concurrent; dynamic PGO; invariant globalization; no EventSource.
- `DOTNET_GCgen0size=64MB`: a request allocates a few KB, and with the default gen0 budget server GC ran
  several gen0 collections a second.
- No middleware, no routing tables, no DI: `app.Run(Router.Handle)` and a `switch` on the path segments.
- Responses are written by `Utf8JsonWriter` into a pooled buffer and sent with one `Content-Length` write. The
  pool keeps at most 32 MB of buffers, so a burst doesn't pin its peak for good.
- **Text columns go out as UTF-8.** A text column's bytes are copied from the row (`GetBytes`, a buffer kept per
  thread) and written as they are, escaped where JSON needs it: no string per column and no UTF-16 round trip.
- Npgsql's SQL rewriting is off (`Npgsql.EnableSqlRewriting=false`): every statement uses `$n` and none holds a
  `;`, so nothing needs parsing.
- Writing text as UTF-8 cut the bytes allocated per request (our code and Npgsql, measured in process on the full
  seed) from about 5.9 KB to 3 KB on the request mix; a global list page went from 9.8 KB to 3.5 KB. Gen0
  collections came half as often; throughput and latency on a 2-vCPU x86 VPS stayed within the runs' spread.
- JWT verification allocates nothing: it checks the token's alphabet, decodes into stack buffers, signs with a
  keyed HMAC kept per thread, compares in constant time, and accepts only HS256 tokens with a numeric `sub`
  and an unexpired `exp`. A header equal to the one this server issues skips the header parse; any other is
  parsed and must say HS256. An `Authorization` header sent empty is a 401 like any other invalid one; only a
  request without the header is anonymous.
- **A stats line** every `STATS_SECONDS` on stdout: reads queued, read groups (average and largest size), reruns
  and refusals; writes queued and their longest wait, groups (average and largest size), commit time, reruns,
  refusals; bcrypt queue, wait, lanes per batch, skipped jobs; GC counts and pause time, allocation rate, heap
  size, process CPU, thread pool size and backlog. The reader and writer loops and the bcrypt workers update its
  counters once per group or batch, not per request.
- **Not used: inline socket completions** (`DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1`, continuations on
  the epoll thread). Under full load the CPU per request was the same, and the median login took 5.7 s instead
  of 1.7 s.

## Micro-benchmarks

BenchmarkDotNet 0.15.8 (`Benchmarks/`, short job), on the 8-vCPU x86 target above:

| Benchmark | Time | Allocated |
|---|---|---|
| JWT verify | 1.23 µs | 0 B |
| JWT issue | 1.01 µs | 944 B |
| Query `?limit=20&offset=40` | 76 ns | 64 B |
| Query escaped filter | 61 ns | 48 B |
| Article JSON | 294 ns | 0 B |
| Timestamp | 85 ns | 0 B |

`DbBenchmarks` runs reads against the full seed (`BENCH_DB`), alternating an article by slug and the first page
of the global list, four ways: one command per read on pooled connections, the same multiplexed, one batch with
new commands (the read groups), and one batch reusing its commands. Bytes allocated per read, groups of 32,
prepared statements:

| | Text columns as strings | as UTF-8 bytes |
|---|---|---|
| One command per read | 11.0 KB | 2.9 KB |
| Multiplexed | 10.0 KB | 1.8 KB |
| Batch, new commands (the server) | 9.2 KB | 0.96 KB |
| Batch, reused commands | 8.4 KB | 0.20 KB |

Prepared statements make a single read 4 to 5 times faster (100 vs 514 µs one command per read, 89 vs 362 µs as
a batch). Times compare only within one way of reading: one command per read spreads a group over 32 connections
that run in parallel on an idle server, so its group finishes sooner, while under full load no core is idle and
the CPU per read decides; that is what the load runs above measure.

## Tests

```bash
dotnet test --project Tests/Tests.csproj          # bcrypt, JWT, password queue, query parsing (xUnit v3 + FsCheck)
# with a database the user may create tables in, also the group commit and list tests:
CONDUIT_TEST_DB="Host=127.0.0.1;Username=conduit;Password=conduit;Database=writer_test" dotnet test --project Tests/Tests.csproj
dotnet run -c Release --project Benchmarks -- --filter '*'
bash ../../test/run.sh submissions/csharp-kestrel-xeonfx
```
