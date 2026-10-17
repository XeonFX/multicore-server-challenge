using System;
using Npgsql;
using NpgsqlTypes;

namespace Conduit.Data;

public static class Db
{
    public static NpgsqlDataSource Source = null!;

    /// <summary>Every statement that writes goes through here, grouped into shared commits.</summary>
    public static Writer Writer = null!;

    public static void Init(string databaseUrl, int poolSize, bool multiplexing, int writers)
    {
        var uri = new Uri(databaseUrl);
        string[] userInfo = uri.UserInfo.Split(':', 2);
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            SslMode = SslMode.Disable,
            // Many requests share each connection: commands from concurrent requests are pipelined onto a few
            // connections instead of each request holding one, so Postgres runs few busy backends.
            Multiplexing = multiplexing,
            MaxPoolSize = poolSize,
            NoResetOnClose = true,
            Enlist = false,
        };
        Plan(csb);
        Source = NpgsqlDataSource.Create(csb.ConnectionString);
        // Each writer loop owns one connection; a multiplexed one would make the reads queued on it wait for the
        // commit's WAL flush.
        csb.Multiplexing = false;
        csb.MaxPoolSize = writers;
        Writer = new Writer(NpgsqlDataSource.Create(csb.ConnectionString));
        Writer.Start(writers);
    }

    /// <summary>How every connection prepares and plans statements; the list tests run under the same settings.</summary>
    public static void Plan(NpgsqlConnectionStringBuilder csb)
    {
        // Every statement is prepared once per connection (Parse/plan once, then only Bind/Execute).
        csb.MaxAutoPrepare = 64;
        csb.AutoPrepareMinUsages = 2;
        // Session settings. The reference queries take LIMIT/OFFSET as parameters, which makes Postgres's plan cache
        // keep choosing custom plans and re-plan every execution. Our statements are written so the generic plan is
        // the right plan, so it is planned once per connection. JIT and parallel workers never pay off for statements
        // this short. The statement timeout replaces Npgsql's client-side one (CommandTimeout 0), which costs a timer
        // per command.
        csb.CommandTimeout = 0;
        csb.Options = "-c plan_cache_mode=force_generic_plan -c jit=off -c max_parallel_workers_per_gather=0 -c statement_timeout=30s";
    }

    public static NpgsqlBatchCommand Command(string sql, params ReadOnlySpan<NpgsqlParameter> parameters)
    {
        var c = new NpgsqlBatchCommand(sql);
        foreach (var p in parameters) c.Parameters.Add(p);
        return c;
    }

    public static NpgsqlParameter<long> P(long v) => new() { TypedValue = v };
    public static NpgsqlParameter<string> P(string v) => new() { TypedValue = v };
    public static NpgsqlParameter<string[]> P(string[] v) => new() { TypedValue = v };
    public static NpgsqlParameter Text(string? v) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)v ?? DBNull.Value };
}
