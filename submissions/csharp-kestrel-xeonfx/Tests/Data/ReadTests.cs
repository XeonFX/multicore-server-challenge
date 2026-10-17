using System;
using System.Linq;
using System.Threading.Tasks;
using Conduit.Data;
using Npgsql;
using Xunit;

// The timeouts turn a read that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051

namespace Conduit.Tests.Data;

/// <summary>
/// Pooled read commands against a real Postgres (CONDUIT_TEST_DB), with multiplexing (the server's default) and
/// without: a command is reused, never shared by two reads at once, and stays usable after a failed statement.
/// </summary>
public sealed class ReadTests : IAsyncDisposable
{
    static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CONDUIT_TEST_DB");
    NpgsqlDataSource? _source;

    NpgsqlDataSource Source(bool multiplexing)
    {
        Assert.SkipWhen(ConnectionString is null, "CONDUIT_TEST_DB is not set");
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { Multiplexing = multiplexing, MaxPoolSize = 4 };
        Db.Plan(csb);
        _source = NpgsqlDataSource.Create(csb.ConnectionString);
        using (_source.OpenConnection()) { } // connected once, as Db.Init does
        return _source;
    }

    static async Task<string> Run(Read read, long n, string s)
    {
        using var lease = read.Rent().Set(0, n).Set(1, s);
        await using var r = await lease.Command.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return r.GetString(0);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReturnedCommandIsUsedAgainWithTheNewValues(bool multiplexing)
    {
        var read = new Read(Source(multiplexing), "SELECT $1::text || ':' || $2", typeof(long), typeof(string));
        NpgsqlCommand first;
        using (var lease = read.Rent()) first = lease.Command;
        Assert.Equal("1:a", await Run(read, 1, "a"));
        Assert.Equal("2:b", await Run(read, 2, "b"));
        using var again = read.Rent();
        Assert.Same(first, again.Command);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentReadsEachGetTheirOwnCommandAndAnswer(bool multiplexing)
    {
        var read = new Read(Source(multiplexing), "SELECT $1::text || ':' || $2 FROM pg_sleep(0.001)", typeof(long), typeof(string));
        // in waves: after the first, every wave rents from a pool of returned commands, all at once
        for (int wave = 0; wave < 25; wave++)
        {
            var ids = Enumerable.Range(wave * 100, 20).ToArray();
            var results = await Task.WhenAll(ids.Select(i => Task.Run(() => Run(read, i, "v" + i))));
            Assert.Equal(ids.Select(i => $"{i}:v{i}"), results);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACommandWhoseStatementFailedServesTheNextRead(bool multiplexing)
    {
        // division by zero for 0, an answer otherwise
        var read = new Read(Source(multiplexing), "SELECT (10 / $1)::text || $2", typeof(long), typeof(string));
        var e = await Assert.ThrowsAsync<PostgresException>(() => Run(read, 0, "x"));
        Assert.Equal(PostgresErrorCodes.DivisionByZero, e.SqlState);
        Assert.Equal("5y", await Run(read, 2, "y"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null) await _source.DisposeAsync();
    }
}
