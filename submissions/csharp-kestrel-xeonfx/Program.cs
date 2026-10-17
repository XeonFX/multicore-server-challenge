using System;
using System.Net;
using Conduit.Data;
using Conduit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Conduit;

public static class Program
{
    public static void Main(string[] args)
    {
        string dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";
        string secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "";
        if (dbUrl.Length == 0 || secret.Length == 0)
        {
            Console.Error.WriteLine("DATABASE_URL and JWT_SECRET are required");
            Environment.Exit(1);
        }
        string host = Env("HOST", "127.0.0.1");
        int port = int.Parse(Env("PORT", "3000"));
        int cores = Environment.ProcessorCount;
        // Two Postgres backends per core: at 4 cores 8 beat 4 and 16 by 8-10% CPU per request (more backends
        // contend for the same cores; fewer leave reads queued behind each other on a multiplexed connection).
        int poolSize = int.Parse(Env("DB_POOL_SIZE", (2 * cores).ToString()));
        // One per two cores. Under load jobs queue up and a worker runs up to four interleaved, so fewer threads
        // spend less CPU per hash; but when every core is busy each thread gets only its share of the CPU, and
        // logins grow with the cores: at 4 cores one thread let two thirds of the logins fail, two kept up.
        int bcryptThreads = int.Parse(Env("BCRYPT_THREADS", Math.Max(1, cores / 2).ToString()));
        int bcryptNice = int.Parse(Env("BCRYPT_NICE", "0"));
        // How long the first login of a batch waits for others to share its interleaved run. Logins are about one
        // request in a thousand, so the wait never shows in the p95; at the workload's few logins a second it lets
        // most hashes share a run. 0, 80 and 200 ms were within noise at 12k users on 4 CPUs.
        var bcryptWindow = TimeSpan.FromMilliseconds(int.Parse(Env("BCRYPT_BATCH_MS", "200")));
        bool multiplexing = Env("DB_MULTIPLEXING", "1") != "0";
        // Writer loops, each with its own connection, each committing whatever writes queued while it was busy.
        int writers = int.Parse(Env("DB_WRITERS", "2"));
        if (writers < 1 || bcryptThreads < 1)
        {
            // with none, every write or every login would wait forever
            Console.Error.WriteLine("DB_WRITERS and BCRYPT_THREADS must be at least 1");
            Environment.Exit(1);
        }

        Jwt.Init(secret);
        Db.Init(dbUrl, poolSize, multiplexing, writers);
        Passwords.Init(bcryptThreads, bcryptWindow, bcryptNice);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = args });
        // The default backlog of 512 overflows while tens of thousands of users connect during the ramp; the kernel
        // caps it at net.core.somaxconn.
        builder.WebHost.UseSockets(o => o.Backlog = 65535);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            // Browsers (and the load generator's users) keep one idle connection for up to a 70 s think time,
            // and Nginx's upstream keep-alive is 60 s: idle connections stay open well past both.
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            o.Limits.MaxRequestBodySize = 1 << 20;
            o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1);
            if (IPAddress.TryParse(host, out var ip)) o.Listen(ip, port);
            else if (host == "localhost") o.ListenLocalhost(port);
            else o.ListenAnyIP(port);
        });

        var app = builder.Build();
        app.Run(Router.Handle);
        Console.WriteLine($"conduit (c#) listening on {host}:{port}, db pool {poolSize}{(multiplexing ? " multiplexed" : "")}, {writers} writers, bcrypt threads {bcryptThreads} at nice {bcryptNice}, batch window {bcryptWindow.TotalMilliseconds} ms");
        app.Run();
    }

    static string Env(string name, string fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }
}
