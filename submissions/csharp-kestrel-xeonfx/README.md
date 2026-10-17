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

Environment: `DATABASE_URL`, `JWT_SECRET` (required), `HOST` (127.0.0.1), `PORT` (3000), `DB_POOL_SIZE`
(2 per core), `DB_MULTIPLEXING` (1), `DB_WRITERS` (2), `BCRYPT_THREADS` (one per two cores, at least 1), `BCRYPT_NICE` (0),
`BCRYPT_BATCH_MS` (200), `DOTNET_GCgen0size` (64 MB, set in `start.sh`).

## Layout

| Path | Namespace | What |
|---|---|---|
| `Program.cs`, `Router.cs` | `Conduit` | settings from the environment, Kestrel, the route switch |
| `Http/` | `Conduit.Http` | request bodies, query strings, pooled JSON responses |
| `Endpoints/` | `Conduit.Endpoints` | the handlers: users, profiles, articles, comments, tags |
| `Data/` | `Conduit.Data` | Npgsql data sources, the SQL, the group commit writer |
| `Security/` | `Conduit.Security` | JWT, the `Authorization` header, bcrypt and its threads |
| `Tests/`, `Benchmarks/` | | their own projects; `Tests/` mirrors the folders above |

## Results

The most users each server held while staying under the bar (p95 < 500 ms, p99 < 1 s, errors < 1%), stepping
the user count up on the full seed. The server and Postgres ran together on N CPUs and the load generator on
the others. Go and Rust are [`examples/go`](../../examples/go) and [`examples/rust`](../../examples/rust)
unchanged, on the same Postgres 18 with the same config.

| CPUs | C# | Go | Rust | C# req/s at its limit |
|---|---|---|---|---|
| 1 (x86-64 VPS) | **1,250** | 700 | 500 | 2,160 |
| 2 (arm64) | **5,500** | 2,500 | 2,500 | 11,150 |
| 4 (arm64) | **13,000** | 5,000 | 5,000 | 20,730 |
| 6 (arm64) | **12,000** | 6,000 | 6,000 | 22,460 |

Users think for a tenth of the workload's 7 s (`-think-scale 0.1`), so these counts are not the scoring box's;
they rank the servers under the same load. Every login and sign-up succeeded in every C# run up to its limit.
The 1-CPU box is a 2-vCPU OVH VPS; 2 to 6 CPUs are a Mac mini M1 in a Linux VM. 8 CPUs needs a separate
load generator machine, which I didn't have. On the mini, CPUs 5 and 6 are efficiency cores and the load
generator had two cores left, so the 6-CPU row understates every server.

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

- **Multiplexing.** Npgsql pipelines commands from concurrent requests onto few connections, so Postgres runs
  a few busy backends instead of many mostly idle ones. On 1 CPU at 800 users: 17% more requests per second
  and 15% less CPU per request than one connection per request (median of 3 rounds each).
- **Group commit.** Every write goes to a queue. Two writer loops, each on its own connection, send what has
  queued (up to 128 writes) as one `NpgsqlBatch`: one round trip and one transaction, so one WAL flush commits
  all of them. Each write's response is built from its row inside the batch and sent only after the commit
  (rule 6). If a statement fails, the transaction rolls back; the writes before it commit together, it fails
  with its own error (a duplicate email fails only its own sign-up, 422), and the writes after it are tried as
  the next group. The result is that of running them one by one in order, and a write runs at most twice before
  it commits (three times if the writes before the failing one then fail together and each runs alone). A write
  that loses a deadlock to the other loop's group runs again. A write whose client has gone is dropped if no
  group has taken it yet. A broken connection fails the writes it held, since whether they committed is
  unknown, and the loop reconnects. On 4 CPUs at 12,000 users: 19,100 to 22,500 requests per second, app CPU
  per request 0.073 to 0.053 ms, Postgres 0.134 to 0.124 ms.
- **No reset on close, no enlistment, no TLS** on the loopback connection.

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
  hashed, so under overload the CPU goes to the logins still waiting.
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
- JWT verification allocates nothing: it checks the token's alphabet, decodes into stack buffers, signs with a
  keyed HMAC kept per thread, compares in constant time, and accepts only HS256 tokens with a numeric `sub`
  and an unexpired `exp`. A header equal to the one this server issues skips the header parse; any other is
  parsed and must say HS256.
- **Not used: inline socket completions** (`DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1`, continuations on
  the epoll thread). Under full load the CPU per request was the same, and the median login took 5.7 s instead
  of 1.7 s.

## Micro-benchmarks

BenchmarkDotNet 0.15.8 (`Benchmarks/`), arm64 Linux:

| Benchmark | Time | Allocated |
|---|---|---|
| JWT verify | 1.29 µs | 0 B |
| JWT issue | 1.14 µs | 944 B |
| Query `?limit=20&offset=40` | 111 ns | 168 B |
| Query escaped filter | 75 ns | 80 B |
| Article JSON | 482 ns | 0 B |
| Timestamp | 61 ns | 0 B |

## Tests

```bash
dotnet test --project Tests/Tests.csproj          # bcrypt, JWT, password queue, query parsing (xUnit v3 + FsCheck)
# with a database the user may create tables in, also the group commit and list tests:
CONDUIT_TEST_DB="Host=127.0.0.1;Username=conduit;Password=conduit;Database=writer_test" dotnet test --project Tests/Tests.csproj
dotnet run -c Release --project Benchmarks -- --filter '*'
bash ../../test/run.sh submissions/csharp-kestrel-xeonfx
```
