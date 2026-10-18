using System.ComponentModel.DataAnnotations;
using Conduit.Auth;
using Conduit.Data;

namespace Conduit.Api;

// Request bodies wrap their fields in "user", "article" or "comment". The attributes are checked before a handler
// runs; a request that fails them gets 422. In an update, a field left out (null) stays as it is.

public record RegisterRequest([Required] Registration User);

public record Registration([Required] string Username, [Required] string Email, [Required, FitsBcrypt] string Password);

public record LoginRequest([Required] Credentials User);

public record Credentials([Required] string Email, [Required] string Password);

public record UpdateUserRequest([Required] UserChanges User);

public record UserChanges(
    [MinLength(1)] string? Email,
    [MinLength(1)] string? Username,
    [MinLength(1), FitsBcrypt] string? Password,
    string? Bio,
    string? Image)
{
    public void ApplyTo(User user)
    {
        user.Email = Email ?? user.Email;
        user.Username = Username ?? user.Username;
        user.Bio = Bio ?? user.Bio;
        user.Image = Image ?? user.Image;
        if (Password is not null) user.PasswordHash = Passwords.Hash(Password);
    }
}

public record NewArticleRequest([Required] NewArticle Article);

public record NewArticle([Required] string Title, [Required] string Description, [Required] string Body, [MaxLength(100), NoEmptyItems] string[]? TagList);

public record UpdateArticleRequest([Required] ArticleChanges Article);

public record ArticleChanges([MinLength(1)] string? Title, [MinLength(1)] string? Description, [MinLength(1)] string? Body)
{
    public void ApplyTo(Article article)
    {
        if (Title is not null) article.SetTitle(Title);
        article.Description = Description ?? article.Description;
        article.Body = Body ?? article.Body;
        article.UpdatedAt = DateTime.UtcNow;
    }
}

public record NewCommentRequest([Required] NewComment Comment);

public record NewComment([Required] string Body);

/// <summary>GET /api/articles filters. Only the first one given applies, in this order.</summary>
public record ArticleFilter(string? Tag, string? Author, string? Favorited)
{
    public IQueryable<Article> Apply(ConduitDb db)
    {
        if (!string.IsNullOrEmpty(Tag)) return db.Tags.Where(t => t.Name == Tag).SelectMany(t => t.Articles);
        if (!string.IsNullOrEmpty(Author)) return db.Articles.Where(a => a.Author.Username == Author);
        if (!string.IsNullOrEmpty(Favorited)) return db.Favorites.Where(f => f.User.Username == Favorited).Select(f => f.Article);
        return db.Articles;
    }
}

/// <summary>bcrypt reads only the first 72 bytes of a password, so a longer one is refused.</summary>
public sealed class FitsBcryptAttribute() : ValidationAttribute("{0} can be at most 72 bytes")
{
    public override bool IsValid(object? value) => value is not string password || Passwords.CanHash(password);
}

public sealed class NoEmptyItemsAttribute() : ValidationAttribute("{0} can't contain empty values")
{
    public override bool IsValid(object? value) => value is not string[] items || items.All(item => item is { Length: > 0 });
}
