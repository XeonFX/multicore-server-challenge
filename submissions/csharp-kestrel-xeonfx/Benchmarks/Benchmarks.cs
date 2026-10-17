using System;
using BenchmarkDotNet.Attributes;
using Conduit.Http;
using Conduit.Security;
using Microsoft.AspNetCore.Http;

namespace Conduit.Benchmarks;

/// <summary>Cost 10, as the server runs it: one login or sign-up.</summary>
[MemoryDiagnoser]
public class BcryptBenchmarks
{
    const string Password = "password123";
    const string SeedHash = "$2a$10$ZSfEoPvlcsj2XfvsSeK25em4NZ9Jm7ntGqXuLgQfHEeZPYB8899wq";

    [Benchmark(Baseline = true)]
    public bool BCryptNetNext() => BCrypt.Net.BCrypt.Verify(Password, SeedHash);

    [Benchmark]
    public bool Ours() => Bcrypt.Verify(Password, SeedHash);

    /// <summary>Two verifications interleaved on one thread; reported per hash.</summary>
    [Benchmark(OperationsPerInvoke = 2)]
    public bool OursInterleavedPair()
    {
        BcryptJob a = Job(), b = Job();
        Bcrypt.Run(a, b);
        return a.Matches() & b.Matches();
    }

    [Benchmark(OperationsPerInvoke = 3)]
    public bool OursInterleavedThree()
    {
        BcryptJob a = Job(), b = Job(), c = Job();
        Bcrypt.Run(a, b, c);
        return a.Matches() & b.Matches() & c.Matches();
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public bool OursInterleavedFour()
    {
        BcryptJob a = Job(), b = Job(), c = Job(), d = Job();
        Bcrypt.Run(a, b, c, d);
        return a.Matches() & b.Matches() & c.Matches() & d.Matches();
    }

    static BcryptJob Job() => BcryptJob.ForVerify(Password, SeedHash)!;
}

[MemoryDiagnoser]
public class JwtBenchmarks
{
    string _token = "";

    [GlobalSetup]
    public void Setup()
    {
        Jwt.Init("multicore-server-challenge");
        _token = Jwt.Issue(12345);
    }

    [Benchmark]
    public long Verify() => Jwt.Verify(_token);

    [Benchmark]
    public string Issue() => Jwt.Issue(12345);
}

[MemoryDiagnoser]
public class QueryBenchmarks
{
    static readonly QueryString Plain = new("?limit=20&offset=40");
    static readonly QueryString Filtered = new("?tag=lorem%20ipsum&limit=10&offset=0");

    [Benchmark]
    public long Page() => Query.TryPage(Plain, out long limit, out long offset) ? limit + offset : -1;

    [Benchmark]
    public string? EscapedFilter() => Query.Get(Filtered, "tag");
}

[MemoryDiagnoser]
public class JsonBenchmarks
{
    static readonly DateTime When = new(2025, 4, 1, 12, 0, 0, 123, DateTimeKind.Utc);

    /// <summary>One article's worth of output, as Articles.Write emits it: text as UTF-8, as it comes from the row (here from
    /// literals, not a reader).</summary>
    [Benchmark]
    public int Article()
    {
        var j = JsonOut.Rent();
        var w = j.W;
        w.WriteStartObject();
        w.WriteStartObject(Names.Article);
        w.WriteString(Names.Slug, "how-to-train-your-dragon-12345"u8);
        w.WriteString(Names.Title, "How to train your dragon"u8);
        w.WriteString(Names.Description, "Ever wonder how?"u8);
        w.WriteString(Names.Body, "It takes a Jacobian, and a lot of patience. Then some more patience."u8);
        JsonFields.WriteTimestamp(w, Names.CreatedAt, When);
        JsonFields.WriteTimestamp(w, Names.UpdatedAt, When);
        w.WritePropertyName(Names.TagList);
        w.WriteRawValue("""["dragons","training"]"""u8, skipInputValidation: true);
        w.WriteNumber(Names.FavoritesCount, 42);
        w.WriteBoolean(Names.Favorited, false);
        w.WriteStartObject(Names.Author);
        w.WriteString(Names.Username, "jake"u8);
        w.WriteString(Names.Bio, "I work at statefarm"u8);
        w.WriteNull(Names.Image);
        w.WriteBoolean(Names.Following, false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
        int n = j.Written.Length;
        j.Dispose();
        return n;
    }

    [Benchmark]
    public void Timestamp()
    {
        var j = JsonOut.Rent();
        j.W.WriteStartObject();
        JsonFields.WriteTimestamp(j.W, Names.CreatedAt, When);
        j.W.WriteEndObject();
        j.Dispose();
    }
}
