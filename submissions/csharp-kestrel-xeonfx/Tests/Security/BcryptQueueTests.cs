using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Security;
using Xunit;

namespace Conduit.Tests.Security;

public class BcryptQueueTests
{
    const string SeedHash = "$2a$10$ZSfEoPvlcsj2XfvsSeK25em4NZ9Jm7ntGqXuLgQfHEeZPYB8899wq";

    static BcryptJob Job(string password = "password123") => BcryptJob.ForVerify(password, SeedHash)!;

    [Fact]
    public async Task JobsArrivingWithinTheWindowShareOneBatch()
    {
        var q = new BcryptQueue(1, TimeSpan.FromMilliseconds(500));
        var first = q.RunAsync(Job(), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var later = q.RunAsync(Job("password124"), TestContext.Current.CancellationToken);
        var last = q.RunAsync(Job(), TestContext.Current.CancellationToken);

        Assert.True((await first).Matches());
        Assert.False((await later).Matches());
        Assert.True((await last).Matches());
        Assert.Equal(1, q.Batches);
    }

    [Fact]
    public async Task AFullBatchRunsWithoutWaitingOutTheWindow()
    {
        var q = new BcryptQueue(1, TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        var all = Enumerable.Range(0, Bcrypt.MaxLanes).Select(_ => q.RunAsync(Job(), TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(all);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(1, q.Batches);
    }

    [Fact]
    public async Task WithoutAWindowABacklogStillRunsAsOneBatch()
    {
        var q = new BcryptQueue(1, TimeSpan.Zero);
        // the worker may take the first job alone before the rest are queued; the rest then wait as one backlog
        var all = Enumerable.Range(0, Bcrypt.MaxLanes).Select(_ => q.RunAsync(Job(), TestContext.Current.CancellationToken)).ToArray();
        foreach (var t in all) Assert.True((await t).Matches());
        Assert.InRange(q.Batches, 1, 2);
    }

    [Fact]
    public async Task JobsPastTheQueueLimitAreRefusedAtOnce()
    {
        var q = new BcryptQueue(1, TimeSpan.Zero);
        int extra = 2 * Bcrypt.MaxLanes;
        var all = Enumerable.Range(0, BcryptQueue.MaxQueuedPerThread + extra)
            .Select(_ => q.RunAsync(Job(), TestContext.Current.CancellationToken)).ToArray();

        // While they are queued (well under a hash's time) the worker takes one batch at most, so that many of the
        // extra jobs find room and the rest are refused.
        var refused = all.Where(t => t.IsFaulted).ToArray();
        Assert.InRange(refused.Length, extra - Bcrypt.MaxLanes, extra);
        foreach (var t in refused) await Assert.ThrowsAsync<ServerBusyException>(() => t);
        foreach (var t in all.Where(t => !t.IsFaulted)) Assert.True((await t).Matches());
        Assert.Equal(0, q.Queued);
    }

    [Fact]
    public async Task AnAbortedFirstJobDoesNotStartTheWindowOrABatch()
    {
        var q = new BcryptQueue(1, TimeSpan.FromMilliseconds(300));
        var aborted = q.RunAsync(Job(), new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        var sw = Stopwatch.StartNew();
        Assert.True((await q.RunAsync(Job(), TestContext.Current.CancellationToken)).Matches());
        Assert.Equal(1, q.Batches);
        // the live job waited its own window, measured from its own arrival
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250), $"took {sw.Elapsed}");
    }
}
