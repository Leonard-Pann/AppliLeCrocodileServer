using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LeCrocodile.Reviews.Api;

public sealed class CertificateRestartService : BackgroundService
{
    private readonly IConfiguration configuration;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<CertificateRestartService> logger;

    public CertificateRestartService(IConfiguration configuration, IHostApplicationLifetime lifetime,
        ILogger<CertificateRestartService> logger)
    {
        this.configuration = configuration;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string path = configuration["CertificateReload:Path"] ?? throw new InvalidOperationException("Certificate reload path is required.");
        string host = configuration["CertificateReload:Host"] ?? throw new InvalidOperationException("Certificate reload host is required.");
        string? password = configuration["CertificateReload:Password"];
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(path, stoppingToken));
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, stoppingToken);
                if (CryptographicOperations.FixedTimeEquals(original, SHA256.HashData(bytes)))
                {
                    continue;
                }

                using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(bytes, password, X509KeyStorageFlags.EphemeralKeySet);
                if (!certificate.HasPrivateKey || !certificate.MatchesHostname(host) ||
                    DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                {
                    logger.LogError("Changed certificate is unusable for {Host}; retaining the running server and retrying.", host);
                    continue;
                }

                logger.LogInformation("A valid replacement certificate was detected; stopping for the container restart policy.");
                lifetime.StopApplication();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                logger.LogError(exception, "Unable to read the replacement certificate; retaining the running server and retrying.");
            }
        }
    }
}
