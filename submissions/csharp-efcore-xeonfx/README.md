# C# with ASP.NET Core minimal APIs and Entity Framework Core

Front: direct

The plain way to write Conduit in C#: minimal APIs, EF Core with LINQ, the standard JWT bearer handler and
BCrypt.Net-Next. It is written to be short and easy to read, not to be fast. For the fast one, see
[`csharp-kestrel-xeonfx`](../csharp-kestrel-xeonfx).

| | Version |
|---|---|
| .NET | 10 (SDK 10.0.402) |
| ASP.NET Core | 10, minimal APIs on Kestrel |
| EF Core | Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, EFCore.NamingConventions 10.0.1 |
| JWT | Microsoft.AspNetCore.Authentication.JwtBearer 10.0.13 |
| bcrypt | BCrypt.Net-Next 4.2.0, cost 10 |

## Choices

No raw SQL and no hand-tuning. The only performance choices are the ones a normal app makes:

- Responses are LINQ projections, so EF Core loads each view in one query.
- `DbContextPool`, and a connection pool of 90 to stay under Postgres's 100.
- EF Core's SQL logging is off. At Information level it logs every statement.

## Results

**12,000 users** passed a 5-minute hold: p95 125 ms, p99 287 ms, no errors, 3,152 requests per second.

The target was a Google Cloud `c4-highcpu-8` (8 vCPU, 16 GB) set up with `box/setup-box.sh`, on the full seed,
serving port 80 directly. The load generator was a second `c4-highcpu-8` in the same zone, at think-scale 1. Each
probe had a 60 s ramp and 60 s measured, after a 3-minute warm-up at 500 users. The database was fresh at the start
and not reset between runs.

| Users | Run | p95 | p99 | Errors | Target CPU | Result |
|---:|---|---:|---:|---:|---:|---|
| 1,000 | probe | 9 ms | 29 ms | 0% | 9% | pass |
| 2,000 | probe | 8 ms | 25 ms | 0% | 17% | pass |
| 4,000 | probe | 9 ms | 30 ms | 0% | 35% | pass |
| 8,000 | probe | 17 ms | 46 ms | 0% | 75% | pass |
| 12,000 | 7 probes | 89 to 915 ms | 210 to 1,230 ms | under 0.1% | 95% | 6 passed, 1 failed |
| 13,000 | probe | 661 ms | 893 ms | 0.39% | 96% | fail |
| 16,000 | probe | 2,020 ms | 2,677 ms | 0.45% | 96% | fail |
| **12,000** | **5-minute hold** | **125 ms** | **287 ms** | **0%** | **95%** | **pass** |

Above 8,000 users the CPU is full and throughput stays near 3,100 requests per second, so latency rises instead.
12,000 is the edge: one of the seven short probes there failed. In the hold, the server used 774 MB.

The slowest endpoints in the hold were login and sign-up (p95 716 and 772 ms, bcrypt on the request thread) and
creating an article (p99 2.8 s: it saves new tags one at a time, then the article).

For comparison, on the same machine type:
[`csharp-kestrel-xeonfx`](../csharp-kestrel-xeonfx) reached 100,000 users and the Go example 30,000.
