using Conduit.Auth;
using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class ArticlesApi
{
    const int MaxCount = 1000;

    public static RouteGroupBuilder MapArticlesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/articles", List).AuthOptional();
        api.MapGet("/articles/feed", Feed).AuthRequired();
        api.MapGet("/articles/{slug}", Get).AuthOptional();
        api.MapPost("/articles", Create).AuthRequired();
        api.MapPut("/articles/{slug}", Update).AuthRequired();
        api.MapDelete("/articles/{slug}", Delete).AuthRequired();
        api.MapPost("/articles/{slug}/favorite", Favorite).AuthRequired();
        api.MapDelete("/articles/{slug}/favorite", Unfavorite).AuthRequired();
        return api;
    }

    static Task<Ok<ArticlesResponse>> List(
        [AsParameters] ArticleFilter filter, Page page, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        Paged(filter.Apply(db), page, viewer, ct);

    static Task<Ok<ArticlesResponse>> Feed(Page page, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        Paged(db.Follows.Where(f => f.FollowerId == viewer.Id).SelectMany(f => f.Followee.Articles), page, viewer, ct);

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Get(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var article = await db.Articles
            .Where(a => a.Slug == slug)
            .Select(Projections.Article(viewer.Id))
            .SingleOrDefaultAsync(ct);
        if (article is null) return Errors.NotFound;

        return TypedResults.Ok(new ArticleResponse(article));
    }

    static async Task<Created<ArticleResponse>> Create(NewArticleRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var draft = request.Article;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var article = new Article
        {
            AuthorId = viewer.Id,
            Description = draft.Description,
            Body = draft.Body,
            Tags = await db.TagsNamedAsync(draft.TagList ?? [], ct),
        };
        await db.Articles.AddAsync(article, ct); // gives the article its id
        article.SetTitle(draft.Title);           // the slug ends with that id
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var view = await Find(db, article.Id, viewer, ct);
        return TypedResults.Created($"/api/articles/{article.Slug}", new ArticleResponse(view));
    }

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Update(
        string slug, UpdateArticleRequest request, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var article = await db.Articles.SingleOrDefaultAsync(a => a.Slug == slug, ct);
        if (article is null) return Errors.NotFound;
        if (article.AuthorId != viewer.Id) return Errors.Forbidden;

        request.Article.ApplyTo(article);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new ArticleResponse(await Find(db, article.Id, viewer, ct)));
    }

    static async Task<Results<NoContent, ProblemHttpResult>> Delete(string slug, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var authorId = await db.Articles.Where(a => a.Slug == slug).Select(a => (long?)a.AuthorId).SingleOrDefaultAsync(ct);
        if (authorId is null) return Errors.NotFound;
        if (authorId != viewer.Id) return Errors.Forbidden;

        await db.Articles.Where(a => a.Slug == slug).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    static Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Favorite(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFavorite(slug, favorite: true, viewer, db, ct);

    static Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> Unfavorite(
        string slug, Viewer viewer, ConduitDb db, CancellationToken ct) =>
        ChangeFavorite(slug, favorite: false, viewer, db, ct);

    static async Task<Results<Ok<ArticleResponse>, ProblemHttpResult>> ChangeFavorite(
        string slug, bool favorite, Viewer viewer, ConduitDb db, CancellationToken ct)
    {
        var articleId = await db.ArticleIdAsync(slug, ct);
        if (articleId is null) return Errors.NotFound;

        if (favorite) await db.FavoriteAsync(viewer.Id, articleId.Value, ct);
        else await db.UnfavoriteAsync(viewer.Id, articleId.Value, ct);

        return TypedResults.Ok(new ArticleResponse(await Find(db, articleId.Value, viewer, ct)));
    }

    /// <summary>One page, newest first, and the total capped at 1,000.</summary>
    static async Task<Ok<ArticlesResponse>> Paged(IQueryable<Article> articles, Page page, Viewer viewer, CancellationToken ct)
    {
        var list = await articles
            .OrderByDescending(a => a.Id)
            .Skip(page.Offset)
            .Take(page.Limit)
            .Select(Projections.Article(viewer.Id, includeBody: false))
            .ToListAsync(ct);
        var count = await articles.Take(MaxCount).CountAsync(ct);

        return TypedResults.Ok(new ArticlesResponse(list, count));
    }

    static Task<ArticleView> Find(ConduitDb db, long id, Viewer viewer, CancellationToken ct) =>
        db.Articles.Where(a => a.Id == id).Select(Projections.Article(viewer.Id)).SingleAsync(ct);
}
