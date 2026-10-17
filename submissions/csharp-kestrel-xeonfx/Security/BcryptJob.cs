using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Conduit.Security;

/// <summary>One bcrypt computation: a new hash (random salt) or a check against a stored hash.</summary>
public sealed class BcryptJob
{
    internal readonly uint[] KeyWords = new uint[18];
    internal readonly uint[] SaltWords = new uint[18];
    internal readonly byte[] Hash = new byte[Bcrypt.HashBytes];
    internal readonly int Cost;
    readonly byte[] _salt;
    readonly string? _stored; // Verify: the stored hash

    BcryptJob(ReadOnlySpan<byte> key, byte[] salt, int cost, string? stored)
    {
        _salt = salt;
        Cost = cost;
        _stored = stored;
        StreamWords(key, KeyWords);
        StreamWords(salt, SaltWords);
    }

    /// <summary>A job hashing <paramref name="password"/> with a random salt; null when the password exceeds 72 bytes.</summary>
    public static BcryptJob? ForHash(string password, int cost, byte[]? salt = null)
    {
        if (cost is < 4 or > 31) throw new ArgumentOutOfRangeException(nameof(cost));
        Span<byte> key = stackalloc byte[Bcrypt.MaxKeyBytes + 1];
        int keyLen = Key(password, key);
        if (keyLen < 0) return null;
        if (salt is null) RandomNumberGenerator.Fill(salt = new byte[Bcrypt.SaltBytes]);
        else if (salt.Length != Bcrypt.SaltBytes) throw new ArgumentException("bcrypt salts are 16 bytes", nameof(salt));
        return new BcryptJob(key[..keyLen], salt, cost, null);
    }

    /// <summary>A job checking <paramref name="password"/> against <paramref name="hash"/>; null when the hash is malformed or the password exceeds 72 bytes.</summary>
    public static BcryptJob? ForVerify(string password, string hash)
    {
        // $2a$10$ + 22 salt chars + 31 hash chars
        if (hash.Length != 60 || hash[0] != '$' || hash[1] != '2' || hash[2] is not ('a' or 'b' or 'y') || hash[3] != '$'
            || hash[6] != '$' || !char.IsAsciiDigit(hash[4]) || !char.IsAsciiDigit(hash[5]))
            return null;
        int cost = (hash[4] - '0') * 10 + (hash[5] - '0');
        if (cost is < 4 or > 31) return null;
        var salt = new byte[Bcrypt.SaltBytes];
        if (!Bcrypt.Decode(hash.AsSpan(7, 22), salt)) return null;
        Span<byte> key = stackalloc byte[Bcrypt.MaxKeyBytes + 1];
        int keyLen = Key(password, key);
        if (keyLen < 0) return null;
        return new BcryptJob(key[..keyLen], salt, cost, hash);
    }

    /// <summary>The computed hash as "$2a$NN$" + salt + hash.</summary>
    public string Encoded() => string.Create(60, this, static (s, job) =>
    {
        "$2a$".CopyTo(s);
        s[4] = (char)('0' + job.Cost / 10);
        s[5] = (char)('0' + job.Cost % 10);
        s[6] = '$';
        Bcrypt.Encode(job._salt, s.Slice(7, 22));
        Bcrypt.Encode(job.Hash, s.Slice(29, 31));
    });

    /// <summary>
    /// Whether the computed hash equals the stored one, compared in constant time as encoded text (as Go's bcrypt
    /// does: a stored hash with stray low bits in its last character does not match).
    /// </summary>
    public bool Matches()
    {
        if (_stored is null) return false;
        Span<char> encoded = stackalloc char[31];
        Bcrypt.Encode(Hash, encoded);
        return CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(encoded), MemoryMarshal.AsBytes(_stored.AsSpan(29, 31)));
    }

    /// <summary>The key bytes: the UTF-8 password and its terminating NUL. -1 when the password exceeds 72 bytes.</summary>
    static int Key(string password, Span<byte> key)
    {
        if (!Encoding.UTF8.TryGetBytes(password, key[..Bcrypt.MaxKeyBytes], out int n)) return -1;
        key[n] = 0;
        return n + 1;
    }

    /// <summary>
    /// The 18 words the key schedule reads from <paramref name="data"/>, taken cyclically as OpenBSD's
    /// Blowfish_stream2word does. Each expansion restarts the stream, so the same words recur every time.
    /// </summary>
    static void StreamWords(ReadOnlySpan<byte> data, uint[] words)
    {
        int j = 0;
        for (int i = 0; i < 18; i++)
        {
            uint w = 0;
            for (int k = 0; k < 4; k++)
            {
                w = w << 8 | data[j];
                if (++j == data.Length) j = 0;
            }
            words[i] = w;
        }
    }
}
