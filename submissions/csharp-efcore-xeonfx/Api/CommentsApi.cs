using Conduit.Auth;
using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class CommentsApi
{
    const int MaxComments = 100;

    public static RouteGroupBuilder MapCommentsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/articles/{slug}/comments", List).AuthOptional();
        api.MapPost("/articles/{slug}/comments", Create).AuthRequired();
        api.MapDelete("/articles/{slug}/comments/{id:long}", Delete).AuthRequired();
        return api;
    }

    static async Task<Results<Ok<CommentsResponse>, ProblemHttpResult>> List(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var articleId = await db.ArticleIdAsync(slug, ct);
        if (articleId is null) return Errors.NotFound;

        var comments = await db.Comments
            .Where(c => c.ArticleId == articleId)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(MaxComments)
            .Select(Projections.Comment(viewer.Id))
            .ToListAsync(ct);

        return TypedResults.Ok(new CommentsResponse(comments));
    }

    static async Task<Results<Created<CommentResponse>, ProblemHttpResult>> Create(
        string slug, NewCommentRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var articleId = await db.ArticleIdAsync(slug, ct);
        if (articleId is null) return Errors.NotFound;

        var author = await db.Users.FindAsync([viewer.Id], ct);
        if (author is null) return Errors.Unauthorized;

        var comment = new Comment { ArticleId = articleId.Value, Author = author, Body = request.Comment.Body };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        var view = CommentView.From(comment, following: false);
        return TypedResults.Created($"/api/articles/{slug}/comments/{comment.Id}", new CommentResponse(view));
    }

    static async Task<Results<NoContent, ProblemHttpResult>> Delete(
        string slug, long id, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var authorId = await db.Comments
            .Where(c => c.Id == id && c.Article.Slug == slug)
            .Select(c => (long?)c.AuthorId)
            .SingleOrDefaultAsync(ct);
        if (authorId is null) return Errors.NotFound;
        if (authorId != viewer.Id) return Errors.Forbidden;

        await db.Comments.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }
}
