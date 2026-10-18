namespace Conduit;

/// <summary>The environment the challenge runs the server with. A missing value stops the server at startup.</summary>
public sealed record ServerSettings(string DatabaseUrl, string JwtSecret, string Host, int Port)
{
    public string Url => $"http://{Host}:{Port}";

    public static ServerSettings From(IConfiguration config) => new(
        DatabaseUrl: Required(config, "DATABASE_URL"),
        JwtSecret: Required(config, "JWT_SECRET"),
        Host: config.GetValue("HOST", "0.0.0.0")!,
        Port: config.GetValue("PORT", 3000));

    static string Required(IConfiguration config, string name) =>
        config[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set the environment variable {name}.");
}
