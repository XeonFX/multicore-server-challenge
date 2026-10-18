using Conduit;
using Conduit.Api;
using Conduit.Auth;
using Conduit.Data;

var builder = WebApplication.CreateBuilder(args);
var settings = ServerSettings.From(builder.Configuration);

builder.WebHost.UseUrls(settings.Url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddConduitDb(settings.DatabaseUrl)
    .AddTokenAuth(settings.JwtSecret)
    .AddRealWorldErrors()
    .AddValidation()
    .AddOpenApi()
    .ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new UtcMillisConverter()));
builder.Services.AddHealthChecks().AddDbContextCheck<ConduitDb>();

var app = builder.Build();

app.UseRealWorldErrors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapHealthChecks("/health");

app.MapGroup("/api")
    .MapUsersApi()
    .MapProfilesApi()
    .MapArticlesApi()
    .MapCommentsApi()
    .MapTagsApi();

app.MapFallback(() => Errors.NotFound);

app.Run();
