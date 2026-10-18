using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Conduit.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Conduit.Auth;

/// <summary>RealWorld auth: "Authorization: Token &lt;jwt&gt;", HS256, exactly {"sub": "&lt;user id&gt;", "iat", "exp"}.</summary>
public static class TokenAuth
{
    const string Scheme = "Token ";
    const string OptionalPolicy = "optional";

    public static string? ReadToken(HttpRequest request) =>
        request.Headers.Authorization.ToString() is var header && header.StartsWith(Scheme) ? header[Scheme.Length..] : null;

    /// <summary>A missing or invalid token gives 401.</summary>
    public static RouteHandlerBuilder AuthRequired(this RouteHandlerBuilder route) => route.RequireAuthorization();

    /// <summary>No token means anonymous, but a token that is sent must be valid.</summary>
    public static RouteHandlerBuilder AuthOptional(this RouteHandlerBuilder route) => route.RequireAuthorization(OptionalPolicy);

    public static IServiceCollection AddTokenAuth(this IServiceCollection services, string secret)
    {
        var key = new SymmetricSecurityKey(SigningKey(secret));
        services.AddSingleton(new TokenIssuer(key));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new()
            {
                IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero,
            };
            jwt.Events = new()
            {
                OnMessageReceived = context =>
                {
                    context.Token = ReadToken(context.Request);
                    if (context.Token is null) context.NoResult();
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    if (!HasStringSubject((JsonWebToken)context.SecurityToken) || Viewer.IdOf(context.Principal!) == 0)
                        context.Fail("sub is not a user id");
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    return Errors.Unauthorized.ExecuteAsync(context.HttpContext);
                },
            };
        });

        services.AddAuthorizationBuilder().AddPolicy(OptionalPolicy, policy => policy.RequireAssertion(context =>
            context.User.Identity?.IsAuthenticated == true ||
            context.Resource is HttpContext http && !http.Request.Headers.ContainsKey("Authorization")));

        return services;
    }

    /// <summary>The spec's sub is a string ("42"). IdentityModel turns a number (42) into the same claim, so look at the JSON.</summary>
    static bool HasStringSubject(JsonWebToken token)
    {
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.EncodedPayload));
        return payload.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String;
    }

    /// <summary>
    /// IdentityModel refuses HS256 keys under 32 bytes, and the challenge's secret is 26. HMAC pads a short key with
    /// zeros to its 64-byte block anyway, so padding it to 32 bytes here gives exactly the same signatures.
    /// </summary>
    static byte[] SigningKey(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length < 32) Array.Resize(ref bytes, 32);
        return bytes;
    }
}

public class TokenIssuer(SecurityKey key)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    readonly SigningCredentials credentials = new(key, SecurityAlgorithms.HmacSha256);
    readonly JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };

    public string Issue(long userId) => handler.CreateToken(new SecurityTokenDescriptor
    {
        Claims = new Dictionary<string, object> { ["sub"] = userId.ToString() },
        IssuedAt = DateTime.UtcNow,
        Expires = DateTime.UtcNow + Lifetime,
        SigningCredentials = credentials,
    });
}
