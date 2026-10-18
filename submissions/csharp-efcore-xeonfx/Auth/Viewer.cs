using System.Globalization;
using System.Reflection;
using System.Security.Claims;

namespace Conduit.Auth;

/// <summary>Who is asking: the signed-in user's id and token, or <see cref="Id"/> 0 when anonymous.</summary>
public sealed record Viewer(long Id, string Token) : IBindableFromHttpContext<Viewer>
{
    /// <summary>The "sub" claim as a user id; 0 when it isn't a whole number.</summary>
    public static long IdOf(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("sub")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public static ValueTask<Viewer?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult<Viewer?>(new(IdOf(context.User), TokenAuth.ReadToken(context.Request) ?? ""));
}
