using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Security;

/// <summary>
/// bcrypt (cost 10) on a <see cref="BcryptQueue"/>. A hash takes tens of milliseconds of CPU; running it on the
/// thread pool would let a burst of logins occupy every pool thread and stall all other requests until the pool grows.
/// </summary>
public static class Passwords
{
    const int Cost = 10;

    static BcryptQueue _queue = null!;

    /// <summary>A hash of a random password, checked when a login names an unknown email so that it costs the same
    /// bcrypt as a wrong password: response times don't tell which emails have accounts.</summary>
    public static readonly string UnknownUserHash = Bcrypt.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Cost);

    public static void Init(int threads, TimeSpan batchWindow = default, int nice = 0) => _queue = new BcryptQueue(threads, batchWindow, nice);

    /// <summary>Hashes waiting for a worker.</summary>
    public static int Queued => _queue.Queued;

    /// <summary>bcrypt ignores everything after 72 bytes; such passwords are refused, not truncated.</summary>
    public static bool TooLong(string password) => Encoding.UTF8.GetByteCount(password) > Bcrypt.MaxKeyBytes;

    /// <summary>A new hash; the caller has checked <see cref="TooLong"/>. Cancelled, unhashed, if
    /// <paramref name="aborted"/> fires before a worker takes the job.</summary>
    public static async Task<string> HashAsync(string password, CancellationToken aborted) =>
        (await _queue.RunAsync(BcryptJob.ForHash(password, Cost) ?? throw new ArgumentException("password is too long", nameof(password)), aborted)).Encoded();

    /// <summary>False for a wrong password, a password over 72 bytes, or a malformed stored hash.</summary>
    public static async Task<bool> VerifyAsync(string password, string hash, CancellationToken aborted) =>
        BcryptJob.ForVerify(password, hash) is { } job && (await _queue.RunAsync(job, aborted)).Matches();
}
