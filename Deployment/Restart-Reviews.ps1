param(
    [Parameter(Mandatory)][string]$DeploymentDirectory,
    [switch]$EnsureStarted
)

$ErrorActionPreference = 'Stop'
try
{
    $settings = Get-Content (Join-Path $DeploymentDirectory 'hosting.json') -Raw | ConvertFrom-Json
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $settings.CertificatePath, '', [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try
    {
        if (!$certificate.HasPrivateKey -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow.AddHours(1))
        {
            throw 'The certificate is missing its private key or expires within one hour.'
        }
        $expectedThumbprint = $certificate.Thumbprint
    }
    finally { $certificate.Dispose() }

    if ($settings.Mode -eq 'docker')
    {
        $docker = $settings.DockerExecutable
        $compose = @('--context', 'desktop-linux', 'compose', '--project-directory', $settings.ProjectDirectory,
            '--env-file', (Join-Path $settings.ProjectDirectory '.env'),
            '-f', (Join-Path $settings.ProjectDirectory 'compose.yaml'))
        # Migration must create this marker only after a verified import. Never start an empty production volume.
        if (!(Test-Path (Join-Path $DeploymentDirectory 'docker-import-verified.txt')))
        {
            throw 'Docker startup is blocked until database import has been verified.'
        }
        if ($EnsureStarted)
        {
            & $docker @compose up -d --no-build reviews
        }
        else { & $docker @compose restart --timeout 30 reviews }
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE." }
    }
    elseif ($settings.Mode -eq 'native')
    {
        $executable = Join-Path $settings.NativeDirectory 'LeCrocodile.Reviews.Api.exe'
        $servers = @(Get-CimInstance Win32_Process -Filter "Name = 'LeCrocodile.Reviews.Api.exe'" |
            Where-Object { $_.ExecutablePath -eq $executable })
        if (!$EnsureStarted)
        {
            foreach ($server in $servers) { Stop-Process -Id $server.ProcessId }
            $servers = @()
        }
        if ($servers.Count -eq 0)
        {
            Start-Process -FilePath $executable -WorkingDirectory $settings.NativeDirectory -WindowStyle Hidden `
                -ArgumentList '--environment Production' | Out-Null
        }
    }
    else { throw 'Hosting mode must be native or docker.' }

    $uri = [Uri]$settings.HealthUri
    if ($uri.Scheme -ne 'https') { throw 'The health URI must use HTTPS.' }
    $lastFailure = ''
    for ($attempt = 0; $attempt -lt 15; $attempt++)
    {
        try
        {
            $response = Invoke-RestMethod -Uri $uri.AbsoluteUri -TimeoutSec 5
            if ($response.status -ne 'Healthy') { throw 'The HTTPS health check failed.' }
            $client = [System.Net.Sockets.TcpClient]::new()
            try
            {
                $connect = $client.ConnectAsync($uri.DnsSafeHost, $uri.Port)
                if (!$connect.Wait(5000)) { throw 'TLS connection timed out.' }
                $tls = [System.Net.Security.SslStream]::new($client.GetStream(), $false)
                try
                {
                    $tls.ReadTimeout = 5000
                    $tls.WriteTimeout = 5000
                    # Default trust and hostname checks remain enabled. No validation callback is installed.
                    $tls.AuthenticateAsClient($uri.DnsSafeHost)
                    $remote = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($tls.RemoteCertificate)
                    try
                    {
                        if ($remote.Thumbprint -ne $expectedThumbprint) { throw 'Kestrel still serves a different certificate.' }
                    }
                    finally { $remote.Dispose() }
                }
                finally { $tls.Dispose() }
            }
            finally { $client.Dispose() }
            Write-Output 'HTTPS is healthy and Kestrel serves the expected certificate.'
            exit 0
        }
        catch { $lastFailure = $_.Exception.Message; Start-Sleep -Seconds 2 }
    }
    throw "Post-restart verification failed: $lastFailure"
}
catch
{
    Write-Error $_
    exit 1
}
