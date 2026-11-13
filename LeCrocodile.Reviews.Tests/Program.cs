using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using LeCrocodile.Reviews.Api;
using LeCrocodile.Reviews.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LeCrocodile.Reviews.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    private static int assertions;

    public static async Task Main()
    {
        VerifyRollingWindow();
        string directory = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyRepositoryAsync(Path.Combine(directory, "repository.db"));
            await VerifyHttpsApiAsync(Path.Combine(directory, "api.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine($"Passed {assertions} backend checks.");
    }

    private static void VerifyRollingWindow()
    {
        ManualTimeProvider clock = new ManualTimeProvider();
        using RollingWindowRateLimiter limiter = new RollingWindowRateLimiter(clock);
        for (int index = 0; index < 5; index++)
        {
            using RateLimitLease lease = limiter.AttemptAcquire();
            Check(lease.IsAcquired, "The first five requests fit the rolling window.");
        }

        using (RateLimitLease rejected = limiter.AttemptAcquire())
        {
            Check(!rejected.IsAcquired && rejected.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan delay)
                && delay == TimeSpan.FromSeconds(1), "The sixth request is rejected with retry metadata.");
        }

        clock.Advance(TimeSpan.FromMilliseconds(999));
        using (RateLimitLease boundary = limiter.AttemptAcquire())
        {
            Check(!boundary.IsAcquired, "Requests cannot burst across a fixed-window boundary.");
        }

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using RateLimitLease renewed = limiter.AttemptAcquire();
        Check(renewed.IsAcquired, "A permit becomes available exactly one second after acceptance.");
    }

    private static async Task VerifyRepositoryAsync(string databasePath)
    {
        ReviewRepository repository = new ReviewRepository(databasePath);
        repository.Initialize();
        Guid firstUser = Guid.NewGuid();
        ReviewResponse first = repository.Save("ABBAYE", firstUser, new SubmitReviewRequest(" A ", 1, " First "), default);
        ReviewResponse updated = repository.Save("ABBAYE", firstUser, new SubmitReviewRequest("B", 5, "Changed"), default);
        Check(first.Id == updated.Id && first.CreatedAt == updated.CreatedAt, "Upserts preserve review identity and creation date.");
        ReviewPage own = repository.GetPage("ABBAYE", firstUser, null, 20, default);
        Check(own.TotalCount == 1 && own.AverageRating == 5 && own.Items[0].IsOwnReview,
            "An installation has one rating per cocktail and can update it.");

        Guid otherUser = Guid.NewGuid();
        repository.Save("ABBAYE", otherUser, new SubmitReviewRequest("Other", 1, "O'Brien; DROP TABLE Reviews;"), default);
        ReviewPage firstPage = repository.GetPage("ABBAYE", firstUser, null, 1, default);
        Check(firstPage.TotalCount == 2 && firstPage.AverageRating == 3 && firstPage.Items.Length == 1
            && firstPage.NextBefore is not null && !firstPage.Items[0].IsOwnReview,
            "Pagination keeps the full rating aggregate and separates installations.");
        repository.Save("ABBAYE", Guid.NewGuid(), new SubmitReviewRequest("New", 3, "Inserted between page reads"), default);
        ReviewPage secondPage = repository.GetPage("ABBAYE", firstUser, firstPage.NextBefore, 1, default);
        Check(secondPage.Items[0].Id == first.Id && secondPage.NextBefore is null,
            "Keyset pagination neither repeats nor skips older reviews when new ones arrive.");
        ReviewRepository reopened = new ReviewRepository(databasePath);
        reopened.Initialize();
        Check(reopened.GetPage("ABBAYE", firstUser, null, 20, default).TotalCount == 3,
            "SQL data survives repository recreation and schema initialization is repeatable.");
        Check(reopened.GetPage("ACAPULCO", firstUser, null, 20, default) is { TotalCount: 0, AverageRating: 0 },
            "Cocktail reviews remain isolated and empty results are explicit.");
        ReviewSummary summary = reopened.GetSummaries(default).Single();
        Check(summary.CocktailId == "ABBAYE" && summary.TotalCount == 3 && summary.AverageRating == 3,
            "Summaries contain the global average without downloading comments.");

        Task[] writes = Enumerable.Range(0, 12).Select(index => Task.Run(() => repository.Save(
            "ACAPULCO", firstUser, new SubmitReviewRequest("Concurrent", index % 5 + 1, $"Write {index}"), default))).ToArray();
        await Task.WhenAll(writes);
        Check(reopened.GetPage("ACAPULCO", firstUser, null, 20, default).TotalCount == 1,
            "Concurrent submissions cannot create duplicate votes for an installation.");
        using CancellationTokenSource canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool cancellationObserved = false;
        try
        {
            repository.GetSummaries(canceled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        Check(cancellationObserved, "Canceled repository work is not started.");
        string backupPath = databasePath + ".backup.db";
        repository.Backup(backupPath);
        ReviewRepository backup = new ReviewRepository(backupPath);
        Check(backup.GetPage("ABBAYE", firstUser, null, 20, default).TotalCount == 3,
            "The online SQLite backup includes committed WAL data.");
        bool overwriteRejected = false;
        try
        {
            repository.Backup(backupPath);
        }
        catch (IOException)
        {
            overwriteRejected = true;
        }

        Check(overwriteRejected, "Creating a backup cannot overwrite an existing file.");
    }

    private static async Task VerifyHttpsApiAsync(string databasePath)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using X509Certificate2 generatedCertificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(
            generatedCertificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        await using WebApplication server = CreateServer(databasePath, certificate);
        await server.StartAsync();
        string[] addresses = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.ToArray();
        using HttpClientHandler handler = new HttpClientHandler
        {
            // Trust only this test's ephemeral certificate. Production clients use normal platform certificate validation.
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote?.Thumbprint == certificate.Thumbprint
        };
        using HttpClient client = new HttpClient(handler) { BaseAddress = new Uri(addresses.Single(url => url.StartsWith("https:"))) };
        Guid userId = Guid.NewGuid();
        string path = $"/api/v1/cocktails/ABBAYE/reviews?userId={userId:D}";
        using (HttpResponseMessage emptyResponse = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, path)))
        {
            ReviewPage empty = await ReadAsync<ReviewPage>(emptyResponse);
            Check(emptyResponse.StatusCode == HttpStatusCode.OK && empty.TotalCount == 0,
                "An HTTPS client can read an empty cocktail review page.");
            Check(emptyResponse.Headers.CacheControl?.NoStore == true, "Review responses cannot be cached as stale community data.");
        }

        using (HttpResponseMessage response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new SubmitReviewRequest("  Visitor  ", 4, "  Très bon 🍹  "), options: JsonOptions)
        }))
        {
            ReviewResponse saved = await ReadAsync<ReviewResponse>(response);
            Check(response.StatusCode == HttpStatusCode.OK && saved.UserName == "Visitor" && saved.Comment == "Très bon 🍹",
                "HTTPS submissions persist trimmed Unicode content.");
        }

        string otherPath = $"/api/v1/cocktails/ABBAYE/reviews?userId={Guid.NewGuid():D}";
        using (HttpResponseMessage response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, otherPath)))
        {
            string json = await response.Content.ReadAsStringAsync();
            ReviewPage page = JsonSerializer.Deserialize<ReviewPage>(json, JsonOptions)!;
            Check(page.TotalCount == 1 && page.AverageRating == 4 && !page.Items[0].IsOwnReview,
                "Other installations immediately see the stored review.");
            Check(!json.Contains(userId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                && !json.Contains(userId.ToString("N"), StringComparison.OrdinalIgnoreCase),
                "Public responses do not disclose installation UUIDs.");
        }

        await ExpectAsync(client, HttpStatusCode.BadRequest, HttpMethod.Get, "/api/v1/cocktails/ABBAYE/reviews");
        await ExpectAsync(client, HttpStatusCode.BadRequest, HttpMethod.Get,
            $"/api/v1/cocktails/ABBAYE/reviews?userId={Guid.Empty:D}");
        await ExpectAsync(client, HttpStatusCode.BadRequest, HttpMethod.Get, path + "&limit=101");
        await ExpectAsync(client, HttpStatusCode.BadRequest, HttpMethod.Get, path + "&before=-1");
        await ExpectAsync(client, HttpStatusCode.NotFound, HttpMethod.Get,
            $"/api/v1/cocktails/UNKNOWN/reviews?userId={userId:D}");
        foreach (SubmitReviewRequest invalid in new SubmitReviewRequest[]
        {
            new SubmitReviewRequest(" ", 3, "Comment"),
            new SubmitReviewRequest(new string('x', 41), 3, "Comment"),
            new SubmitReviewRequest("Visitor", 0, "Comment"),
            new SubmitReviewRequest("Visitor", 6, "Comment"),
            new SubmitReviewRequest("Visitor", 3, " "),
            new SubmitReviewRequest("Visitor", 3, new string('x', 2001)),
            new SubmitReviewRequest("Bad\0Name", 3, "Comment"),
            new SubmitReviewRequest(null!, 3, null!)
        })
        {
            using HttpResponseMessage response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = JsonContent.Create(invalid, options: JsonOptions)
            });
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid review input is rejected without a server error.");
        }

        await ExpectAsync(client, HttpStatusCode.BadRequest, HttpMethod.Put, path, "{not-json}");
        await ExpectAsync(client, HttpStatusCode.RequestEntityTooLarge, HttpMethod.Put, path,
            "{\"comment\":\"" + new string('x', 20000) + "\"}");
        using (HttpResponseMessage summariesResponse = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/cocktails/review-summaries?userId={userId:D}")))
        {
            ReviewSummary[] summaries = await ReadAsync<ReviewSummary[]>(summariesResponse);
            Check(summaries is [{ CocktailId: "ABBAYE", TotalCount: 1, AverageRating: 4 }], "The summary endpoint matches SQL state.");
        }

        using (HttpResponseMessage plaintext = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get,
            addresses.Single(url => url.StartsWith("http:")) + "/health")))
        {
            Check(plaintext.StatusCode == HttpStatusCode.BadRequest, "Plain HTTP is rejected even if an HTTP listener is configured.");
        }

        await Task.Delay(1100);
        Task<HttpResponseMessage>[] burst = Enumerable.Range(0, 6).Select(index =>
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{index + 1}");
            return SendOnceAsync(client, request);
        }).ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(burst);
        Check(responses.Count(response => response.StatusCode == HttpStatusCode.OK) == 5
            && responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests) == 1,
            "The real server limits an IP to five requests, ignoring spoofed forwarding headers.");
        Check(responses.Single(response => response.StatusCode == HttpStatusCode.TooManyRequests).Headers.RetryAfter is not null,
            "Rate-limited clients receive Retry-After.");
        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }

        await server.StopAsync();
        await using WebApplication restarted = CreateServer(databasePath, certificate);
        await restarted.StartAsync();
        string newAddress = restarted.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single(url => url.StartsWith("https:"));
        using HttpResponseMessage afterRestart = await client.GetAsync(newAddress + path);
        Check((await ReadAsync<ReviewPage>(afterRestart)).TotalCount == 1, "Reviews survive a full HTTPS server restart.");
        await restarted.StopAsync();
    }

    private static WebApplication CreateServer(string databasePath, X509Certificate2 certificate)
    {
        return ServerApplication.Build(["--contentRoot", AppContext.BaseDirectory], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = databasePath,
                ["Kestrel:Endpoints:Https:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
                ["Logging:LogLevel:Default"] = "Error",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error"
            });
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        });
    }

    private static async Task ExpectAsync(HttpClient client, HttpStatusCode status, HttpMethod method, string path, string? json = null)
    {
        using HttpResponseMessage response = await SendAsync(client, () => new HttpRequestMessage(method, path)
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json")
        });
        Check(response.StatusCode == status, $"{method} {path.Split('?')[0]} returns {(int)status}.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> createRequest)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await SendOnceAsync(client, createRequest());
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(1050);
        }

        throw new InvalidOperationException("The test client could not obtain a rate-limit permit.");
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            return await client.SendAsync(request);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        assertions++;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
