using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Conduit.Data;

namespace Conduit.Api;

public record UserView(string Email, string Token, string Username, string? Bio, string? Image)
{
    public static UserView From(User user, string token) => new(user.Email, token, user.Username, user.Bio, user.Image);
}

public record ProfileView(string Username, string? Bio, string? Image, bool Following)
{
    public static ProfileView From(User user, bool following) => new(user.Username, user.Bio, user.Image, following);
}

public record ArticleView(
    string Slug,
    string Title,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body,
    List<string> TagList,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Favorited,
    int FavoritesCount,
    ProfileView Author);

public record CommentView(long Id, DateTime CreatedAt, DateTime UpdatedAt, string Body, ProfileView Author)
{
    public static CommentView From(Comment comment, bool following) =>
        new(comment.Id, comment.CreatedAt, comment.UpdatedAt, comment.Body, ProfileView.From(comment.Author, following));
}

/// <summary>Views as queries: EF Core turns these into SQL, so a view is one round trip. 0 is the anonymous viewer.</summary>
public static class Projections
{
    public static Expression<Func<User, ProfileView>> Profile(long viewerId) => user => new(
        user.Username, user.Bio, user.Image,
        user.Followers.Any(f => f.FollowerId == viewerId));

    /// <summary>Lists leave the body out.</summary>
    public static Expression<Func<Article, ArticleView>> Article(long viewerId, bool includeBody = true) => article => new(
        article.Slug,
        article.Title,
        article.Description,
        includeBody ? article.Body : null,
        article.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToList(),
        article.CreatedAt,
        article.UpdatedAt,
        article.Favorites.Any(f => f.UserId == viewerId),
        article.Favorites.Count,
        new(article.Author.Username, article.Author.Bio, article.Author.Image,
            article.Author.Followers.Any(f => f.FollowerId == viewerId)));

    public static Expression<Func<Comment, CommentView>> Comment(long viewerId) => comment => new(
        comment.Id,
        comment.CreatedAt,
        comment.UpdatedAt,
        comment.Body,
        new(comment.Author.Username, comment.Author.Bio, comment.Author.Image,
            comment.Author.Followers.Any(f => f.FollowerId == viewerId)));
}
