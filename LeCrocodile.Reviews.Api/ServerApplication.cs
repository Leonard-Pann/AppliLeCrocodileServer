using System.Net;
using System.Threading.RateLimiting;
using LeCrocodile.Reviews.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace LeCrocodile.Reviews.Api;

public static class ServerApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(TimeProvider.System);
        string catalogPath = Path.Combine(builder.Environment.ContentRootPath, "Data/cocktail-ids.json");
        builder.Services.AddSingleton(new CocktailCatalog(catalogPath));
        string databasePath = builder.Configuration["Database:Path"] ?? "data/reviews.db";
        builder.Services.AddSingleton(new ReviewRepository(Path.GetFullPath(databasePath, builder.Environment.ContentRootPath)));
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                IPAddress? address = context.Connection.RemoteIpAddress;
                string key = address?.MapToIPv6().ToString() ?? "unknown";
                return RateLimitPartition.Get(key, _ => new RollingWindowRateLimiter(TimeProvider.System));
            });
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                await Results.Problem(statusCode: 429, title: "Too many requests. Retry after one second.")
                    .ExecuteAsync(context.HttpContext);
            };
        });

        WebApplication application = builder.Build();
        application.Services.GetRequiredService<ReviewRepository>().Initialize();
        application.UseExceptionHandler(handler => handler.Run(async context =>
        {
            Exception? exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            bool databaseBusy = exception is SqliteException { SqliteErrorCode: 5 or 6 };
            if (databaseBusy)
            {
                context.Response.Headers.RetryAfter = "1";
            }

            await Results.Problem(statusCode: databaseBusy ? 503 : 500,
                title: databaseBusy ? "The database is busy. Retry shortly." : "An unexpected server error occurred.")
                .ExecuteAsync(context);
        }));
        application.UseRateLimiter();
        application.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });

        string minimumVersion = builder.Configuration["Compatibility:MinimumVersion"] ?? "1.0.0";
        if (!Version.TryParse(minimumVersion, out Version? parsedVersion) || parsedVersion.Build < 0 || parsedVersion.Revision >= 0)
        {
            throw new InvalidDataException("Compatibility:MinimumVersion must contain three numeric components.");
        }

        application.MapGet("/api/v1/compatibility", () => Results.Ok(new AppCompatibility(minimumVersion)));
        application.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));
        RouteGroupBuilder api = application.MapGroup("/api/v1/cocktails");
        api.AddEndpointFilter(async (context, next) =>
        {
            string? value = context.HttpContext.Request.Query["userId"];
            if (!Guid.TryParse(value, out Guid userId) || userId == Guid.Empty)
            {
                return Results.Problem(statusCode: 400, title: "A nonempty UUID userId query parameter is required.");
            }

            return await next(context);
        });
        api.MapGet("/alcohol-summaries", (ReviewRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(repository.GetAlcoholSummaries(cancellationToken)));
        api.MapGet("/review-summaries", (ReviewRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(repository.GetSummaries(cancellationToken)));
        api.MapGet("/{cocktailId}/reviews", GetReviews);
        api.MapPut("/{cocktailId}/reviews", SaveReview);
        return application;
    }

    private static IResult GetReviews(
        string cocktailId,
        Guid userId,
        long? before,
        int? limit,
        CocktailCatalog catalog,
        ReviewRepository repository,
        CancellationToken cancellationToken)
    {
        if (!catalog.Contains(cocktailId))
        {
            return Results.Problem(statusCode: 404, title: "Unknown cocktail ID.");
        }

        if (before is <= 0 || limit is < 1 or > 100)
        {
            return Results.Problem(statusCode: 400, title: "before must be positive and limit must be between 1 and 100.");
        }

        return Results.Ok(repository.GetPage(cocktailId, userId, before, limit ?? 20, cancellationToken));
    }

    private static IResult SaveReview(
        string cocktailId,
        Guid userId,
        [FromBody] SubmitReviewRequest request,
        CocktailCatalog catalog,
        ReviewRepository repository,
        CancellationToken cancellationToken)
    {
        if (!catalog.Contains(cocktailId))
        {
            return Results.Problem(statusCode: 404, title: "Unknown cocktail ID.");
        }

        Dictionary<string, string[]> errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length > 40
            || request.UserName.Any(char.IsControl))
        {
            errors["userName"] = ["Use 1 to 40 characters without control characters."];
        }

        if (request.Rating is < 1 or > 5)
        {
            errors["rating"] = ["Use an integer between 1 and 5."];
        }

        if (request.Comment is null || request.Comment.Trim().Length > 2000
            || request.Comment.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            errors["comment"] = ["Use at most 2000 characters. Only line breaks and tabs are allowed as control characters."];
        }

        if (request.AlcoholLevel is < 0 or > 2)
        {
            errors["alcoholLevel"] = ["Use 0, 1 or 2."];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(
            repository.Save(cocktailId, userId, request, cancellationToken));
    }
}
