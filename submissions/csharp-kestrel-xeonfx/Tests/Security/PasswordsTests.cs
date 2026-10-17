using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Security;
using Xunit;

namespace Conduit.Tests.Security;

public class PasswordsTests
{
    const string SeedHash = "$2a$10$ZSfEoPvlcsj2XfvsSeK25em4NZ9Jm7ntGqXuLgQfHEeZPYB8899wq";

    static PasswordsTests() => Passwords.Init(1);

    /// <summary>With one worker, jobs submitted together queue up and run in batches of up to four; each caller
    /// still gets the answer for its own password, wherever it landed in a batch.</summary>
    [Fact]
    public async Task EachCallerGetsItsOwnAnswerFromABatch()
    {
        bool[] good = [true, false, true, true, false, false, true, false, true];
        var verifies = good.Select(g => Passwords.VerifyAsync(g ? "password123" : "password124", SeedHash, TestContext.Current.CancellationToken)).ToArray();
        var hashes = new[] { "alpha", "beta", "gamma" }.Select(async p => (p, h: await Passwords.HashAsync(p, TestContext.Current.CancellationToken))).ToArray();

        Assert.Equal(good, await Task.WhenAll(verifies));
        foreach (var (p, h) in await Task.WhenAll(hashes))
        {
            Assert.StartsWith("$2a$10$", h);
            Assert.True(BCrypt.Net.BCrypt.Verify(p, h));
            Assert.False(BCrypt.Net.BCrypt.Verify(p + "x", h));
        }
    }

    /// <summary>A job whose request was aborted before a worker took it is dropped, not hashed: its task is
    /// cancelled (a hashed one would complete), and the live jobs queued around it still get their answers.</summary>
    [Fact]
    public async Task AJobAbortedWhileQueuedIsDroppedAndTheOthersStillRun()
    {
        var aborted = new CancellationToken(canceled: true);
        bool[] live = [true, false, true, false, true, true];
        var tasks = live.Select((g, i) => i % 2 == 0
            ? Passwords.VerifyAsync(g ? "password123" : "password124", SeedHash, aborted)
            : Passwords.VerifyAsync(g ? "password123" : "password124", SeedHash, TestContext.Current.CancellationToken)).ToArray();
        var hash = Passwords.HashAsync("alpha", aborted);

        for (int i = 0; i < tasks.Length; i++)
        {
            if (i % 2 == 0) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks[i]);
            else Assert.Equal(live[i], await tasks[i]);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hash);
    }

    [Fact]
    public async Task UnknownUserHashIsACost10HashNoPasswordMatches()
    {
        Assert.StartsWith("$2a$10$", Passwords.UnknownUserHash);
        Assert.NotNull(BcryptJob.ForVerify("x", Passwords.UnknownUserHash));
        Assert.False(await Passwords.VerifyAsync("password123", Passwords.UnknownUserHash, TestContext.Current.CancellationToken));
        Assert.False(await Passwords.VerifyAsync("", Passwords.UnknownUserHash, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedHashAndOverlongPasswordAreFalseWithoutQueueing()
    {
        Assert.False(await Passwords.VerifyAsync("password123", "not a hash", TestContext.Current.CancellationToken));
        Assert.False(await Passwords.VerifyAsync(new string('a', 73), SeedHash, TestContext.Current.CancellationToken));
        Assert.True(Passwords.TooLong(new string('é', 37)));
        Assert.False(Passwords.TooLong(new string('é', 36)));
    }
}
