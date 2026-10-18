using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Conduit.Data;

public class ConduitDb(DbContextOptions<ConduitDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Follow>().HasKey(f => new { f.FollowerId, f.FolloweeId });
        model.Entity<Follow>().HasOne(f => f.Followee).WithMany(u => u.Followers).HasForeignKey(f => f.FolloweeId);

        model.Entity<Favorite>().HasKey(f => new { f.UserId, f.ArticleId });

        // The slug ends with the id, so EF takes the id from the column's own sequence when the article is added.
        model.HasSequence<long>("articles_id_seq").IncrementsBy(1);
        model.Entity<Article>().Property(a => a.Id).UseHiLo("articles_id_seq");

        model.Entity<Article>().HasMany(a => a.Tags).WithMany(t => t.Articles).UsingEntity(
            "article_tags",
            tag => tag.HasOne(typeof(Tag)).WithMany().HasForeignKey("tag_id"),
            article => article.HasOne(typeof(Article)).WithMany().HasForeignKey("article_id"));
    }

    public Task<long?> ArticleIdAsync(string slug, CancellationToken ct) =>
        Articles.Where(a => a.Slug == slug).Select(a => (long?)a.Id).SingleOrDefaultAsync(ct);

    /// <summary>Returns the tags with these names, creating the missing ones.</summary>
    public async Task<List<Tag>> TagsNamedAsync(string[] names, CancellationToken ct)
    {
        if (names.Length == 0) return [];

        var tags = await Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct);
        var missing = names.Except(tags.Select(t => t.Name)).ToList();
        if (missing.Count == 0) return tags;

        foreach (var name in missing)
        {
            Tags.Add(new Tag { Name = name });
            await SaveIgnoringDuplicatesAsync(ct);
        }
        return await Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct);
    }

    public async Task FollowAsync(long followerId, long followeeId, CancellationToken ct)
    {
        Follows.Add(new Follow { FollowerId = followerId, FolloweeId = followeeId });
        await SaveIgnoringDuplicatesAsync(ct);
    }

    public Task UnfollowAsync(long followerId, long followeeId, CancellationToken ct) =>
        Follows.Where(f => f.FollowerId == followerId && f.FolloweeId == followeeId).ExecuteDeleteAsync(ct);

    public async Task FavoriteAsync(long userId, long articleId, CancellationToken ct)
    {
        Favorites.Add(new Favorite { UserId = userId, ArticleId = articleId });
        await SaveIgnoringDuplicatesAsync(ct);
    }

    public Task UnfavoriteAsync(long userId, long articleId, CancellationToken ct) =>
        Favorites.Where(f => f.UserId == userId && f.ArticleId == articleId).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Saves, taking a row that already exists as success: following, favoriting or creating a tag twice, even in
    /// two requests at the same moment, is not an error.
    /// </summary>
    async Task SaveIgnoringDuplicatesAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.IsUniqueViolation())
        {
            foreach (var row in error.Entries) row.State = EntityState.Detached;
        }
    }
}

public static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueViolation(this DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

public static class ConduitDbSetup
{
    public static IServiceCollection AddConduitDb(this IServiceCollection services, string databaseUrl) =>
        services.AddDbContextPool<ConduitDb>(db => db
            .UseNpgsql(ConnectionString(databaseUrl))
            .UseSnakeCaseNamingConvention()
            // A duplicate that SaveIgnoringDuplicatesAsync expects is not an error. Real failures still reach the
            // exception handler, which logs them.
            .ConfigureWarnings(log => log.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    /// <summary>Turns postgres://user:password@host:port/database into an Npgsql connection string.</summary>
    static string ConnectionString(string databaseUrl)
    {
        var url = new Uri(databaseUrl);
        var credentials = url.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        return new NpgsqlConnectionStringBuilder
        {
            Host = url.Host,
            Port = url.IsDefaultPort ? 5432 : url.Port,
            Database = url.AbsolutePath.Trim('/'),
            Username = credentials[0],
            Password = credentials.ElementAtOrDefault(1),
            MaxPoolSize = 90, // Postgres allows 100 connections; leave a few for psql.
        }.ConnectionString;
    }
}
