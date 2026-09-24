$ErrorActionPreference = 'Stop'
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('foundatio-summary-tests-' + [guid]::NewGuid().ToString('N'))
$summarize = Join-Path $PSScriptRoot '../Messaging/summarize.ps1'
New-Item -ItemType Directory $directory | Out-Null

function Write-Trial([string]$Name, [string]$Mode, [string]$Region, [string]$Transport = 'sqs', [string]$RuntimeHash = 'runtime-one') {
    @{
        Success = $true
        Environment = @{ Runtime = 'test'; CoreClrSha256 = $RuntimeHash; Broker = $(if ($Mode -eq 'live') { 'AWS (live)' } else { 'SQS/SNS custom endpoint' }); AwsMode = $Mode; AwsRegion = $Region }
        Options = @{ Engine = 'foundatio'; Transport = $Transport; Scenario = 'queue'; ProducerConcurrency = 1; ConsumerConcurrency = 1; DeliveryCopies = 1; PayloadBytes = 1024; BatchSize = 1; RatePerSecond = 0; MaxOutstanding = 32; Prefetch = 1; DurationSeconds = 1; WarmupSeconds = 1; MaxMessages = 1000 }
        Measurement = @{ Inputs = 10; InputsPerSecond = 10; DeliveriesPerSecond = 10; DeliveryLatency = @{ P50Milliseconds = 1; P95Milliseconds = 2; P99Milliseconds = 3 }; AllocatedBytesPerInput = 1; CpuMilliseconds = 1; PeakWorkingSetBytes = 1024; Duplicates = 0; Missing = 0 }
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $directory "round-$Name.json")
}

function Assert-Rejected([string]$Message = 'mix AWS modes or regions') {
    try { & $summarize -Directory $directory *> $null }
    catch {
        if ($_.Exception.Message -match $Message) { return }
        throw
    }
    throw 'Summarizer accepted incompatible measurements.'
}

try {
    Write-Trial 'one' 'localstack' 'us-east-1'
    Write-Trial 'two' 'live' 'us-east-1'
    Assert-Rejected

    Write-Trial 'one' 'live' 'us-east-1'
    Write-Trial 'two' 'live' 'eu-west-1'
    Assert-Rejected

    Write-Trial 'two' 'live' 'us-east-1'
    Write-Trial 'memory' '' '' 'memory'
    & $summarize -Directory $directory *> $null
    $rows = @(Import-Csv (Join-Path $directory 'summary.csv'))
    $sqs = @($rows | Where-Object Case -Like 'foundatio/sqs *')
    if ($rows.Count -ne 2 -or $sqs.Count -ne 1 -or $sqs[0].Trials -ne 2) {
        throw 'Summarizer did not preserve compatible AWS trials alongside in-memory trials.'
    }
    $report = Get-Content (Join-Path $directory 'summary.md') -Raw
    if ($report -notmatch 'live' -or $report -notmatch 'us-east-1') {
        throw 'Summary does not identify the AWS mode and region.'
    }
    Write-Trial 'two' 'live' 'us-east-1' 'sqs' 'runtime-two'
    Assert-Rejected 'mix runtime'
    Write-Host 'PASS: mixed AWS targets and runtime binaries rejected; compatible trials grouped and labeled.'
}
finally { Remove-Item -Recurse -Force $directory }
