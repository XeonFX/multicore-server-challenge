# Goose: epoll loops and a hand-written Postgres client

**Front: direct.** The server listens on `HOST:PORT` (`0.0.0.0:80` on the box) and holds every user's keep-alive
connection itself.

Everything is [Goose](https://github.com/aardappel/goose), a language that compiles to C: the HTTP server, the
Postgres wire protocol and SCRAM sign-in, JSON, HS256 JWTs, and bcrypt. No libraries beyond Goose's standard
library and the Linux system calls it wraps.

## Stack

| Piece | Version |
|---|---|
| Goose | commit `f605b5a0`, built from source by `build.sh` |
| C compiler | gcc 13.3 (Ubuntu 24.04), through `GOOSE_CC=gcc` |
| HTTP | own HTTP/1.1 parser and response writer on epoll (`http.goose`, `server.goose`) |
| Postgres driver | own wire-protocol client: SCRAM-SHA-256, extended protocol, binary results (`pg.goose`) |
| JSON | Goose's `json` reader for request bodies; responses written straight into the send buffer |
| JWT | HS256 on Goose's `crypto` HMAC-SHA256 (`jwt.goose`) |
| bcrypt | own cost-10 bcrypt that hashes 5 passwords at once (`bcrypt.goose`) |

## Design

Postgres is the bottleneck on this workload, so the server's job is to stay out of its way and hand it as little
work per request as possible.

1. **One event loop per core group, sharing nothing.** `WORKERS` threads (4) each own a `SO_REUSEPORT` listener, an
   edge-triggered epoll set, their client connections and their own Postgres connections (`PG_READERS` read
   connections, 3, plus one writer). There are no locks on the request path.
2. **Pipelined Postgres.** Requests that arrive in one loop turn are written to a connection as one batch of
   Bind/Execute messages under a single Sync, up to 64. Replies are matched back to requests in order. Reads go
   to the least busy read connection. Writes go to the writer, so a slow write never queues behind reads.
3. **Batched list reads.** Article lists and `GET /api/tags` wait in a pool while a batch from this loop is in flight.
   At the next flush, each kind of list (all, tag, author, favorited) goes out as one statement: the members'
   parameters as arrays through `unnest(...) WITH ORDINALITY`, each member's page in a `LATERAL`, and the rows come
   back in member order with the member number last. When most members ask for the same page, the pages are cut
   first and every distinct article is read once (tags, favorite count, author); `favorited` and `following` stay
   per member. `GET /api/tags` reads waiting together share one execution. With no batch in flight a read goes at once,
   so a quiet server adds no wait. The feed is never batched. The idea comes from `rust-tokio-Scooter1337`.
4. **One statement per request.** Every endpoint's SQL is prepared once per connection (`sql.goose`) and runs as a
   generic plan. Lists and the feed fold their capped count into the page query, toggles and comment writes are
   data-modifying CTEs, and creating an article is one implicit transaction. Global and tag lists get an
   article's favorite count and the viewer's `favorited` from one scan of its favorites; the favorited list and
   comments read the viewer's favorites and followees once per statement; creating an article inserts only the tags
   that don't exist yet.
5. **Binary results, no copies.** Columns are read in binary straight from the receive buffer, and JSON is written
   straight into the response buffer. The tag list comes back from Postgres as JSON text.
   Timestamps are formatted from the binary microsecond value.
6. **Failures are retried or mapped.** If a statement in a batch fails, only the failing request gets the error,
   and the others are re-sent. Deadlocks and serialization failures retry up to 5 times; a unique violation is a
   422, a lock or statement timeout a 503.
7. **bcrypt off the loops.** Sign-ups, logins and password changes queue their hash to `HASHERS` threads (2), at a
   lower priority than the loops. A hasher gathers up to 5 jobs for `BCRYPT_BATCH_MS` (250) and hashes them
   together, about 3.7 times the throughput of hashing one at a time. A login's row is read first, its response
   built, and sent only if the hash matches.
8. **Small per connection.** Each loop receives into one shared buffer. A client connection is a slot in an array,
   and holds buffers of its own only while it has unanswered input or unsent output. At 130,000 connections the
   process uses about 60 MB.

Tuning knobs (environment): `WORKERS`, `PG_READERS`, `HASHERS`, `BCRYPT_BATCH_MS`, `READ_BATCH` (most reads per
batch, 32; 0 turns batching off), `READ_HOLD` (batches in flight before reads wait, 1). The defaults are what the load
tests below picked.

## Results on GCP

Target `c4-highcpu-8` (8 vCPU, 16 GB, Postgres 18.6 tuned by `box/pg-conf.sh`, full seed). Load from two
`c4-highcpu-8` machines in the same zone running `bench/cmd/loadgen`, two processes each, every process aimed at
its own target address so none opens more than 64k connections to one address. Each run is 60 s of ramp and 60 s
measured on a fresh copy of the seed, with the defaults above. "Before" is the build without batched reads and the
SQL changes.

| Users | Build | req/s | p95 ms | p99 ms | Errors | CPU: app / Postgres | Verdict |
|--:|---|--:|--:|--:|--:|---|---|
| 120,000 | before | 31,841 | 6.1 | 11.8 | 0 | 18% / 76% | pass |
| 120,000 | now | 31,770 | 3.7 | 6.7 | 0 | 18% / 72% | pass |
| 130,000 | before | 34,052 | 177 | 240 | 0 | 18% / 80% | pass |
| 130,000 | now | 34,486 | 6.0 | 9.8 | 0 | 19% / 75% | pass |
| 140,000 | before | 34,143 | 623 | 742 | 0 | 19% / 80% | fails p95 |
| 140,000 | now | 36,996 | 10.1 | 21.2 | 0 | 19% / 77% | pass |
| 150,000 | before | 34,074 | 951 | 1,076 | 0 | 20% / 79% | fails p95, p99 |
| 150,000 | now | 39,653 | 20.3 | 55.6 | 0 | 20% / 77% | pass |
| 160,000 | now | 41,249 | 368 | 501 | 0 | 22% / 77% | pass |
| 175,000 | now | 41,500 | 967 | 2,818 | 0 | 23% / 76% | fails p95, p99 |

Before, Postgres saturated at about 34k req/s; now the same CPU serves about 41k, and the limit moved from about
130k users to about 160k. A scored run comes after the warm-up and probes on a database that has grown during them,
so expect the score somewhat lower.

What the probes showed:
- 2 loops with 3 read connections each (6 read and 2 write, the Rust entry's split) leaves Postgres less busy (65%
  to 70%) but has a p99 tail: 1.7 s at 130k and 2.9 s at 150k. 4 loops with 3 each stays the default.
- On `n4-highcpu-8` (same Xeon) at 100k users, the build before fails (22,433 req/s, p95 1,025 ms); now passes
  (26,477 req/s, p95 28 ms).
- Earlier: `PG_READERS=1` fails at 120k with Postgres only 53% busy; `BCRYPT_BATCH_MS=250` instead of 100 fills more
  bcrypt lanes per batch.
