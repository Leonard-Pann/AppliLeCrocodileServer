param([string]$TaskName = 'LeCrocodile-ReloadCertificate')

$ErrorActionPreference = 'Stop'
try
{
    $task = Get-ScheduledTask -TaskName $TaskName
    if ($task.State -eq 'Running') { throw 'A certificate deployment is already running.' }
    $previousRun = (Get-ScheduledTaskInfo -TaskName $TaskName).LastRunTime
    Start-ScheduledTask -TaskName $TaskName
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    do
    {
        Start-Sleep -Seconds 2
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        $state = (Get-ScheduledTask -TaskName $TaskName).State
        if ($info.LastRunTime -gt $previousRun -and $state -ne 'Running')
        {
            if ($info.LastTaskResult -ne 0) { throw "Certificate deployment failed: $($info.LastTaskResult)." }
            Write-Output 'Certificate deployment and HTTPS verification completed.'
            exit 0
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Certificate deployment timed out. Check that the Docker owner is signed in and the engine is running.'
}
catch
{
    Write-Error $_
    exit 1
}
