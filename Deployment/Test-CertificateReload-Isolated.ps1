$ErrorActionPreference = 'Stop'
$docker = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('crocodile-cert-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$volume = 'lecrocodile-certificate-reload-test'
$image = if ($env:RELOAD_TEST_IMAGE) { $env:RELOAD_TEST_IMAGE } else { 'crocodile-reload-test:local' }
$testId = [guid]::NewGuid().ToString('N')
$helper = 'crocodile-cert-helper-' + $testId
$server = 'crocodile-cert-server-' + $testId
try {
    foreach ($number in 1, 2) {
        $key = [System.Security.Cryptography.RSA]::Create(2048)
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=isolated-test-$number", $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
        [System.IO.File]::WriteAllBytes((Join-Path $testDirectory "$number.pfx"), $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
        if ($number -eq 2) { $expected = $certificate.Thumbprint }
        $certificate.Dispose()
        $key.Dispose()
    }
    & $docker volume create $volume | Out-Null
    & $docker run -d --name $helper --user 0 --entrypoint /bin/sh -v "${volume}:/certificates" $image -c 'sleep 600' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start helper.' }
    & $docker cp (Join-Path $testDirectory '1.pfx') "${helper}:/certificates/current.pfx"
    & $docker exec $helper chmod 644 /certificates/current.pfx
    & $docker run -d --name $server --restart unless-stopped -p 127.0.0.1:17443:7443 --tmpfs /tmp -v "${volume}:/certificates:ro" -e CertificateReload__Enabled=true -e CertificateReload__Path=/certificates/current.pfx -e CertificateReload__Host=isolated-test-2 -e Database__Path=/tmp/test.db -e 'Kestrel__Endpoints__Https__Url=https://*:7443' -e Kestrel__Endpoints__Https__Certificate__Path=/certificates/current.pfx $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start test server.' }
    function Get-TestCertificate {
        $client = [System.Net.Sockets.TcpClient]::new('127.0.0.1', 17443)
        $stream = [System.Net.Security.SslStream]::new($client.GetStream(), $false, { $true })
        try {
            $stream.AuthenticateAsClient('localhost')
            return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($stream.RemoteCertificate).Thumbprint
        } finally { $stream.Dispose(); $client.Dispose() }
    }
    $before = $null
    for ($attempt = 0; $attempt -lt 20 -and !$before; $attempt++) {
        try { $before = Get-TestCertificate } catch { Start-Sleep -Seconds 1 }
    }
    if (!$before) { throw 'Test server did not start.' }
    $startedBefore = & $docker inspect $server --format '{{.State.StartedAt}}'
    & $docker exec $helper sh -c 'printf invalid > /certificates/invalid.pfx && mv -f /certificates/invalid.pfx /certificates/current.pfx'
    if ($LASTEXITCODE -ne 0) { throw 'Could not place invalid test certificate.' }
    Start-Sleep -Seconds 13
    if ((Get-TestCertificate) -ne $before -or (& $docker inspect $server --format '{{.State.StartedAt}}') -ne $startedBefore) {
        throw 'Invalid replacement disrupted the running server.'
    }
    Write-Output 'Invalid replacement was rejected without a restart.'
    & $docker cp (Join-Path $testDirectory '2.pfx') "${helper}:/certificates/next.pfx"
    & $docker exec $helper sh -c 'chmod 644 /certificates/next.pfx && mv -f /certificates/next.pfx /certificates/current.pfx'
    if ($LASTEXITCODE -ne 0) { throw 'Could not replace test certificate.' }
    & $docker cp "${helper}:/certificates/current.pfx" (Join-Path $testDirectory 'copied.pfx')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the shared volume.' }
    $copied = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $testDirectory 'copied.pfx'), '')
    if ($copied.Thumbprint -ne $expected) { throw 'The replacement was not visible in the shared volume.' }
    $copied.Dispose()
    Write-Output "Expected after replacement: $expected"
    $after = $before
    for ($attempt = 0; $attempt -lt 20 -and $after -eq $before; $attempt++) {
        Start-Sleep -Seconds 1
        try { $after = Get-TestCertificate } catch { }
    }
    $startedAfter = & $docker inspect $server --format '{{.State.StartedAt}}'
    Write-Output "Before: $before"
    Write-Output "After: $after"
    Write-Output "Same container start time: $($startedBefore -eq $startedAfter)"
    if ($before -eq $after) { throw 'Certificate did not change within 20 seconds.' }
    elseif ($after -eq $expected -and $startedBefore -ne $startedAfter) { Write-Output 'RESULT: Certificate changed after automatic container restart.' }
    else { throw 'Unexpected certificate or container restart.' }
} finally {
    & $docker rm -f $server $helper 2>$null | Out-Null
    Remove-Item -LiteralPath $testDirectory -Recurse -Force
    Write-Output "Test volume retained: $volume"
}








