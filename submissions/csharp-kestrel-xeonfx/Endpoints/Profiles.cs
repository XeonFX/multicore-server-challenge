using System;
using System.Threading.Tasks;
using Conduit.Data;
using Conduit.Http;
using Conduit.Security;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Conduit.Endpoints;

public static class Profiles
{
    static JsonOut Profile(NpgsqlDataReader r, bool following)
    {
        var j = JsonOut.Rent();
        var w = j.W;
        w.WriteStartObject();
        w.WriteStartObject(Names.Profile);
        Rows.WritePerson(w, r, 0);
        w.WriteBoolean(Names.Following, following);
        w.WriteEndObject();
        w.WriteEndObject();
        return j;
    }

    // GET /api/profiles/{username}
    public static async Task Get(HttpContext ctx, string username)
    {
        long viewer = Auth.Viewer(ctx);
        if (viewer < 0) { await Responses.Unauthorized(ctx); return; }
        JsonOut j;
        await using (var cmd = Db.Source.CreateCommand(Sql.Profile))
        {
            cmd.Parameters.Add(Db.P(viewer));
            cmd.Parameters.Add(Db.P(username));
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            if (!await r.ReadAsync()) { await Responses.Error(ctx, 404, "profile not found"); return; }
            j = Profile(r, r.GetBoolean(3));
        }
        await Responses.Send(ctx, 200, j);
    }

    static readonly Func<NpgsqlDataReader?, JsonOut?> Followed = r => r is null ? null : Profile(r, true);
    static readonly Func<NpgsqlDataReader?, JsonOut?> Unfollowed = r => r is null ? null : Profile(r, false);

    // POST and DELETE /api/profiles/{username}/follow
    public static async Task Follow(HttpContext ctx, string username, bool follow)
    {
        long viewer = Auth.Viewer(ctx);
        if (viewer <= 0) { await Responses.Unauthorized(ctx); return; }
        var j = await Db.Writer.Run(Db.Command(follow ? Sql.Follow : Sql.Unfollow, Db.P(viewer), Db.P(username)),
            follow ? Followed : Unfollowed, ctx.RequestAborted);
        if (j is null) await Responses.Error(ctx, 404, "profile not found");
        else await Responses.Send(ctx, 200, j);
    }
}
