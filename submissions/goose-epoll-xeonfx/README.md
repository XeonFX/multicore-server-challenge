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
3. **One statement per request.** Every endpoint's SQL is prepared once per connection (`sql.goose`) and runs as a
   generic plan. Lists and the feed fold their capped count into the page query, toggles and comment writes are
   data-modifying CTEs, and creating an article is one implicit transaction.
4. **Binary results, no copies.** Columns are read in binary straight from the receive buffer, and JSON is written
   straight into the response buffer. The tag list comes back from Postgres as JSON text.
   Timestamps are formatted from the binary microsecond value.
5. **Failures are retried or mapped.** If a statement in a batch fails, only the failing request gets the error,
   and the others are re-sent. Deadlocks and serialization failures retry up to 5 times; a unique violation is a
   422, a lock or statement timeout a 503.
6. **bcrypt off the loops.** Sign-ups, logins and password changes queue their hash to `HASHERS` threads (2), at a
   lower priority than the loops. A hasher gathers up to 5 jobs for `BCRYPT_BATCH_MS` (250) and hashes them
   together, about 3.7 times the throughput of hashing one at a time. A login's row is read first, its response
   built, and sent only if the hash matches.
7. **Small per connection.** Each loop receives into one shared buffer. A client connection is a slot in an array,
   and holds buffers of its own only while it has unanswered input or unsent output. At 130,000 connections the
   process uses about 60 MB.

Tuning knobs (environment): `WORKERS`, `PG_READERS`, `HASHERS`, `BCRYPT_BATCH_MS`. The defaults are what the load
tests below picked.

## Results on GCP

Target `c4-highcpu-8` (8 vCPU, 16 GB, Postgres 18.6 tuned by `box/pg-conf.sh`, full seed), load from one
`c4-highcpu-16` in the same zone running `bench/cmd/loadgen`. One load box can open only about 64k connections to
one address, so the target had two extra addresses and the users were split over three loadgen processes.

Holds are 60 s of ramp and 300 s measured, each on a fresh copy of the seed, with the defaults above.

| Run | Users | req/s | p50 ms | p95 ms | p99 ms | Errors | CPU: app / Postgres |
|---|--:|--:|--:|--:|--:|--:|---|
| hold | 120,000 | 31,754 | 2.4 | 24.7 | 62.2 | 0 | 17% / 74% |
| hold | 130,000 | 33,193 | 128 | 368 | 471 | 0 | 18% / 79% |
| probe (60 s) | 140,000 | 34,635 | 250 | 544 | 692 | 0 | 20% / 79% (fails p95; bcrypt batch 100 ms) |

Postgres saturates at about 130k users. A scored run comes after the warm-up and probes on a database that has
grown during them, so expect the score near 120k to 130k.

What the probes showed:
- `PG_READERS=1` fails at 120k (p95 1.2 s) with Postgres only 53% busy: 8 connections can't keep it fed. 2 and 3
  are the same; 6 loops are no better than 4.
- `BCRYPT_BATCH_MS=250` instead of 100 fills more lanes per batch: at 130k, p95 went from 155 to 50 ms, and login
  p95 from 435 to 379 ms.
- Without caching (rule 5), the rest is Postgres's own work: the feed's capped count and the per-row favorite
  counts are the most expensive queries.
