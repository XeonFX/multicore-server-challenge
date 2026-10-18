using System.Text;

namespace Conduit.Auth;

public static class Passwords
{
    const int WorkFactor = 10;

    /// <summary>Checked when no user has the email, so a wrong email takes as long as a wrong password.</summary>
    public static readonly string NoUserHash = Hash("no user has this password");

    /// <summary>bcrypt reads at most 72 bytes, so a longer password would quietly lose its end.</summary>
    public static bool CanHash(string password) => Encoding.UTF8.GetByteCount(password) <= 72;

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public static bool Matches(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
