using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Data;
using Conduit.Http;
using Conduit.Security;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Conduit.Endpoints;

public static class Users
{
    readonly record struct User(string Username, string Email, string? Bio, string? Image)
    {
        /// <summary>The user from columns username, email, bio, image starting at <paramref name="c"/>.</summary>
        public static User Read(NpgsqlDataReader r, int c) =>
            new(r.GetString(c), r.GetString(c + 1), Rows.NullableString(r, c + 2), Rows.NullableString(r, c + 3));
    }

    static Task Send(HttpContext ctx, int status, in User u, string token)
    {
        var j = JsonOut.Rent();
        var w = j.W;
        w.WriteStartObject();
        w.WriteStartObject(Names.User);
        w.WriteString(Names.Email, u.Email);
        w.WriteString(Names.Token, token);
        w.WriteString(Names.Username, u.Username);
        JsonFields.WriteNullableString(w, Names.Bio, u.Bio);
        JsonFields.WriteNullableString(w, Names.Image, u.Image);
        w.WriteEndObject();
        w.WriteEndObject();
        return Responses.Send(ctx, status, j);
    }

    // POST /api/users
    public static async Task Register(HttpContext ctx)
    {
        using var doc = await Body.ReadAsync(ctx);
        if (!Body.TryWrapper(doc, "user", out var u)) { await Responses.Unprocessable(ctx, "invalid body"); return; }
        if (!Body.TryReqString(u, "username", out string username) | !Body.TryReqString(u, "email", out string email)
            | !Body.TryReqString(u, "password", out string password))
        {
            await Responses.Unprocessable(ctx, "username, email and password are required");
            return;
        }
        if (Passwords.TooLong(password)) { await Responses.Unprocessable(ctx, "password is too long"); return; }
        string hash = await Passwords.HashAsync(password, ctx.RequestAborted);

        long id;
        User user;
        try
        {
            (id, user) = await Db.Writer.Run(Db.Command(Sql.Register, Db.P(username), Db.P(email), Db.P(hash)),
                static r => (r!.GetInt64(0), User.Read(r, 1)), ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await Responses.Unprocessable(ctx, "username or email is already taken");
            return;
        }
        await Send(ctx, 201, user, Jwt.Issue(id));
    }

    // POST /api/users/login
    public static async Task Login(HttpContext ctx)
    {
        using var doc = await Body.ReadAsync(ctx);
        if (!Body.TryWrapper(doc, "user", out var u)) { await Responses.Unprocessable(ctx, "invalid body"); return; }
        if (!Body.TryReqString(u, "email", out string email) | !Body.TryReqString(u, "password", out string password))
        {
            await Responses.Unprocessable(ctx, "email and password are required");
            return;
        }

        long id = 0;
        User user = default;
        string hash = Passwords.UnknownUserHash;
        await using (var cmd = Db.Source.CreateCommand(Sql.Login))
        {
            cmd.Parameters.Add(Db.P(email));
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                id = r.GetInt64(0);
                user = User.Read(r, 1);
                hash = r.GetString(5);
            }
        }
        if (!await Passwords.VerifyAsync(password, hash, ctx.RequestAborted) || id == 0) { await Responses.Error(ctx, 401, "invalid email or password"); return; }
        await Send(ctx, 200, user, Jwt.Issue(id));
    }

    // GET /api/user
    public static async Task Current(HttpContext ctx)
    {
        long viewer = Auth.Viewer(ctx);
        if (viewer <= 0) { await Responses.Unauthorized(ctx); return; }
        await using var cmd = Db.Source.CreateCommand(Sql.CurrentUser);
        cmd.Parameters.Add(Db.P(viewer));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) { await Responses.Unauthorized(ctx); return; }
        await Send(ctx, 200, User.Read(r, 0), Auth.Token(ctx));
    }

    // PUT /api/user
    public static async Task Update(HttpContext ctx)
    {
        long viewer = Auth.Viewer(ctx);
        if (viewer <= 0) { await Responses.Unauthorized(ctx); return; }
        using var doc = await Body.ReadAsync(ctx);
        if (!Body.TryWrapper(doc, "user", out JsonElement u)) { await Responses.Unprocessable(ctx, "invalid body"); return; }
        if (!Body.TryOptString(u, "email", true, out string? email) | !Body.TryOptString(u, "username", true, out string? username)
            | !Body.TryOptString(u, "password", true, out string? password) | !Body.TryOptString(u, "bio", false, out string? bio)
            | !Body.TryOptString(u, "image", false, out string? image))
        {
            await Responses.Unprocessable(ctx, "invalid user fields");
            return;
        }
        string? hash = null;
        if (password is not null)
        {
            if (Passwords.TooLong(password)) { await Responses.Unprocessable(ctx, "password is too long"); return; }
            hash = await Passwords.HashAsync(password, ctx.RequestAborted);
        }

        User? user;
        try
        {
            user = await Db.Writer.Run(
                Db.Command(Sql.UpdateUser, Db.P(viewer), Db.Text(email), Db.Text(username), Db.Text(hash), Db.Text(bio), Db.Text(image)),
                static r => r is null ? (User?)null : User.Read(r, 0), ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await Responses.Unprocessable(ctx, "username or email is already taken");
            return;
        }
        if (user is { } updated) await Send(ctx, 200, updated, Auth.Token(ctx));
        else await Responses.Unauthorized(ctx);
    }
}
