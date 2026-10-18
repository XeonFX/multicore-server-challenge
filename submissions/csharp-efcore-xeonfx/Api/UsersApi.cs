using Conduit.Auth;
using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class UsersApi
{
    public static RouteGroupBuilder MapUsersApi(this RouteGroupBuilder api)
    {
        api.MapPost("/users", Register);
        api.MapPost("/users/login", Login);
        api.MapGet("/user", Current).AuthRequired();
        api.MapPut("/user", Update).AuthRequired();
        return api;
    }

    static async Task<Created<UserResponse>> Register(RegisterRequest request, ConduitDb db, TokenIssuer tokens, CancellationToken ct)
    {
        var user = new User
        {
            Username = request.User.Username,
            Email = request.User.Email,
            PasswordHash = Passwords.Hash(request.User.Password),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created("/api/user", new UserResponse(UserView.From(user, tokens.Issue(user.Id))));
    }

    static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> Login(
        LoginRequest request, ConduitDb db, TokenIssuer tokens, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == request.User.Email, ct);
        var passwordMatches = Passwords.Matches(request.User.Password, user?.PasswordHash ?? Passwords.NoUserHash);
        if (user is null || !passwordMatches) return Errors.Unauthorized;

        return TypedResults.Ok(new UserResponse(UserView.From(user, tokens.Issue(user.Id))));
    }

    static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> Current(Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([viewer.Id], ct);
        if (user is null) return Errors.Unauthorized;

        return TypedResults.Ok(new UserResponse(UserView.From(user, viewer.Token)));
    }

    static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> Update(
        UpdateUserRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([viewer.Id], ct);
        if (user is null) return Errors.Unauthorized;

        request.User.ApplyTo(user);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new UserResponse(UserView.From(user, viewer.Token)));
    }
}
