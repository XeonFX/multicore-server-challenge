using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class TagsApi
{
    public static RouteGroupBuilder MapTagsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/tags", List);
        return api;
    }

    static async Task<Ok<TagsResponse>> List(ConduitDb db, CancellationToken ct)
    {
        var tags = await db.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
        return TypedResults.Ok(new TagsResponse(tags));
    }
}
