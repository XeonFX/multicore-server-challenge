using System;
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
    /// <summary>{"user": ...} from columns username, email, bio, image starting at <paramref name="c"/>, with <paramref name="token"/>.</summary>
    static JsonOut User(NpgsqlDataReader r, int c, ReadOnlySpan<char> token)
    {
        var j = JsonOut.Rent();
        var w = j.W;
        w.WriteStartObject();
        w.WriteStartObject(Names.User);
        Rows.WriteText(w, Names.Email, r, c + 1);
        w.WriteString(Names.Token, token);
        Rows.WriteText(w, Names.Username, r, c);
        Rows.WriteNullableText(w, Names.Bio, r, c + 2);
        Rows.WriteNullableText(w, Names.Image, r, c + 3);
        w.WriteEndObject();
        w.WriteEndObject();
        return j;
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

        JsonOut j;
        try
        {
            j = await Db.Writer.Run(Db.Command(Sql.Register, Db.P(username), Db.P(email), Db.P(hash)),
                static r => User(r!, 1, Jwt.Issue(r!.GetInt64(0))), ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await Responses.Unprocessable(ctx, "username or email is already taken");
            return;
        }
        await Responses.Send(ctx, 201, j);
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

        // The response is built while the row is at hand and sent only if the password matches.
        JsonOut? j = null;
        try
        {
            string hash = Passwords.UnknownUserHash;
            using (var read = Db.Reads.Login.Rent().Set(0, email))
            {
                await using var r = await read.Command.ExecuteReaderAsync(ctx.RequestAborted);
                if (await r.ReadAsync())
                {
                    hash = r.GetString(5);
                    j = User(r, 1, Jwt.Issue(r.GetInt64(0)));
                }
            }
            if (!await Passwords.VerifyAsync(password, hash, ctx.RequestAborted) || j is null) { await Responses.Error(ctx, 401, "invalid email or password"); return; }
            var ok = j;
            j = null;
            await Responses.Send(ctx, 200, ok);
        }
        finally
        {
            j?.Dispose(); // a wrong password, or a failure on the way
        }
    }

    // GET /api/user
    public static async Task Current(HttpContext ctx)
    {
        long viewer = Auth.Viewer(ctx);
        if (viewer <= 0) { await Responses.Unauthorized(ctx); return; }
        using var read = Db.Reads.CurrentUser.Rent().Set(0, viewer);
        await using var r = await read.Command.ExecuteReaderAsync(ctx.RequestAborted);
        if (!await r.ReadAsync()) { await Responses.Unauthorized(ctx); return; }
        await Responses.Send(ctx, 200, User(r, 0, Auth.Token(ctx)));
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

        JsonOut? j;
        try
        {
            string token = Auth.Token(ctx).ToString();
            j = await Db.Writer.Run(
                Db.Command(Sql.UpdateUser, Db.P(viewer), Db.Text(email), Db.Text(username), Db.Text(hash), Db.Text(bio), Db.Text(image)),
                r => r is null ? null : User(r, 0, token), ctx.RequestAborted);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await Responses.Unprocessable(ctx, "username or email is already taken");
            return;
        }
        if (j is not null) await Responses.Send(ctx, 200, j);
        else await Responses.Unauthorized(ctx);
    }
}
