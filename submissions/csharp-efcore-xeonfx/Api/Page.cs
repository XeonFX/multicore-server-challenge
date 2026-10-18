using System.Globalization;
using System.Reflection;

namespace Conduit.Api;

/// <summary>?limit (default 20, 1 to 100) and ?offset (default 0). A value that is sent must be valid, or the answer is 422.</summary>
public sealed record Page(int Limit, int Offset) : IBindableFromHttpContext<Page>
{
    public static ValueTask<Page?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult<Page?>(new(
            Read(context.Request.Query, "limit", fallback: 20, min: 1, max: 100),
            Read(context.Request.Query, "offset", fallback: 0, min: 0, max: int.MaxValue)));

    static int Read(IQueryCollection query, string name, int fallback, int min, int max)
    {
        if (!query.TryGetValue(name, out var values)) return fallback;

        if (int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            return value;

        throw new BadHttpRequestException($"{name} must be a whole number from {min} to {max}");
    }
}
