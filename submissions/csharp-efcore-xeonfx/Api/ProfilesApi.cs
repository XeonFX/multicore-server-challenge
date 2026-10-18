using Conduit.Auth;
using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class ProfilesApi
{
    public static RouteGroupBuilder MapProfilesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/profiles/{username}", Get).AuthOptional();
        api.MapPost("/profiles/{username}/follow", Follow).AuthRequired();
        api.MapDelete("/profiles/{username}/follow", Unfollow).AuthRequired();
        return api;
    }

    static async Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> Get(
        string username, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var profile = await db.Users
            .Where(u => u.Username == username)
            .Select(Projections.Profile(viewer.Id))
            .SingleOrDefaultAsync(ct);
        if (profile is null) return Errors.NotFound;

        return TypedResults.Ok(new ProfileResponse(profile));
    }

    static Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> Follow(
        string username, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFollow(username, follow: true, viewer, db, ct);

    static Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> Unfollow(
        string username, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFollow(username, follow: false, viewer, db, ct);

    static async Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> ChangeFollow(
        string username, bool follow, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username, ct);
        if (user is null) return Errors.NotFound;

        if (follow) await db.FollowAsync(viewer.Id, user.Id, ct);
        else await db.UnfollowAsync(viewer.Id, user.Id, ct);

        return TypedResults.Ok(new ProfileResponse(ProfileView.From(user, following: follow)));
    }
}
