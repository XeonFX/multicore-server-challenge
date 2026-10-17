using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Conduit.Data;
using Conduit.Security;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

// The timeouts turn a request that never completes into a failure instead of a hung run. The requests take their
// client's abort token, which these tests leave unset.
#pragma warning disable xUnit1069, xUnit1051

namespace Conduit.Tests.Endpoints;

/// <summary>Through the router, a write the server can't take now is a 503 with Retry-After, and nothing else fails.</summary>
[Collection(ServerState.Collection)]
public sealed class BusyTests(BusyTests.Data data) : IClassFixture<BusyTests.Data>
{
    public sealed class Data : TestSchema
    {
        public long Author;

        protected override async Task Seed()
        {
            await Exec("""
                INSERT INTO users (username, email, password_hash) VALUES ('author', 'author@x', 'h');
                INSERT INTO articles (slug, author_id, title, description, body)
                    SELECT 'b-' || i, u.id, 'title', 'description', 'body' FROM users u, generate_series(1, 3) i;
                """);
            Author = await Scalar<long>("SELECT id FROM users WHERE username = 'author'");
        }
    }

    async Task<HttpContext> Request(string method, string path, string? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.Authorization = "Token " + Jwt.Issue(data.Author);
        if (body is not null) ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        await Router.Handle(ctx);
        return ctx;
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteWaitingOnALockedRowIs503AndTheWriteAfterItCommits()
    {
        TestSchema.Require();
        await using var holder = await data.Admin.OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand("SELECT id FROM articles WHERE slug = 'b-1' FOR UPDATE", holder, tx))
            await lockRow.ExecuteScalarAsync();

        var running = Db.Writer;
        var writer = new Writer(data.WriterSource());
        Db.Writer = writer; // not started: both writes go into one group
        try
        {
            var update = Request("PUT", "/api/articles/b-1", """{"article":{"body":"new"}}""");
            var favorite = Request("POST", "/api/articles/b-2/favorite");
            writer.Start(1);

            var blocked = await update;
            Assert.Equal(503, blocked.Response.StatusCode);
            Assert.Equal("1", blocked.Response.Headers.RetryAfter.ToString());
            Assert.Equal(200, (await favorite).Response.StatusCode);
        }
        finally
        {
            Db.Writer = running;
            writer.Complete();
        }
        await tx.RollbackAsync();
        Assert.Equal("body", await data.Scalar<string>("SELECT body FROM articles WHERE slug = 'b-1'"));
        Assert.Equal(1, await data.Scalar<long>("SELECT count(*) FROM favorites f JOIN articles a ON a.id = f.article_id WHERE a.slug = 'b-2'"));
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteAgainstAFullQueueIs503AtOnce()
    {
        TestSchema.Require();
        var running = Db.Writer;
        var writer = new Writer(data.WriterSource());
        Db.Writer = writer;
        try
        {
            var filler = Enumerable.Range(0, Writer.MaxQueued).Select(_ => writer.Run(Db.Command("SELECT 1"), r => 0)).ToArray();
            var refused = await Request("POST", "/api/articles/b-3/favorite");
            Assert.Equal(503, refused.Response.StatusCode);
            Assert.Equal("1", refused.Response.Headers.RetryAfter.ToString());

            writer.Start(1); // its data source holds one connection
            await Task.WhenAll(filler);
            Assert.Equal(200, (await Request("POST", "/api/articles/b-3/favorite")).Response.StatusCode);
        }
        finally
        {
            Db.Writer = running;
            writer.Complete();
        }
    }
}
