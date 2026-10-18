using System.Text.RegularExpressions;

namespace Conduit.Data;

public class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public string? Bio { get; set; }
    public string? Image { get; set; }
    public List<Follow> Followers { get; set; } = [];
    public List<Article> Articles { get; set; } = [];
}

public class Follow
{
    public long FollowerId { get; set; }
    public long FolloweeId { get; set; }
    public User Followee { get; set; } = null!;
}

public partial class Article
{
    public long Id { get; set; }
    public string Slug { get; private set; } = "";
    public long AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string Title { get; private set; } = "";
    public required string Description { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Tag> Tags { get; set; } = [];
    public List<Favorite> Favorites { get; set; } = [];

    /// <summary>
    /// Sets the title and the slug made from it: "Hello, World!" with id 42 is "hello-world-42". The slug needs the
    /// id, so call this once the article has one. Same as the spec's SQL:
    /// trim(both '-' from lower(regexp_replace(title, '[^A-Za-z0-9]+', '-', 'g'))) || '-' || id.
    /// </summary>
    public void SetTitle(string title)
    {
        Title = title;
        Slug = $"{NotAlphanumeric().Replace(title, "-").ToLowerInvariant().Trim('-')}-{Id}";
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NotAlphanumeric();
}

public class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Article> Articles { get; set; } = [];
}

public class Favorite
{
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public long ArticleId { get; set; }
    public Article Article { get; set; } = null!;
}

public class Comment
{
    public long Id { get; set; }
    public long ArticleId { get; set; }
    public Article Article { get; set; } = null!;
    public long AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
