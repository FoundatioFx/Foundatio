param(
    [ValidateSet('smoke', 'standard', 'extended', 'soak')][string]$Profile = 'standard',
    [int]$Repetitions = 3,
    [int]$Seconds = 15,
    [int]$Warmup = 3,
    [string[]]$Engines = @('foundatio-memory', 'masstransit-memory', 'foundatio-redis', 'foundatio-sqs', 'masstransit-sqs'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('results/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$DotnetPath = 'dotnet',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Repetitions -lt 1 -or $Repetitions -gt 20) { throw 'Repetitions must be 1-20.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
if (@(Get-ChildItem -Force $OutputDirectory).Count -gt 0) { throw 'OutputDirectory must be empty so previous trials cannot be overwritten or mistaken for new results.' }
if (-not $NoBuild) { & dotnet build (Join-Path $PSScriptRoot 'Foundatio.Messaging.Benchmarks.csproj') -c Release --nologo }
$dll = Join-Path $PSScriptRoot 'bin/Release/net10.0/Foundatio.Messaging.Benchmarks.dll'
if (-not (Test-Path $dll)) { throw 'Build the benchmark before using -NoBuild.' }
& $DotnetPath --info | Set-Content (Join-Path $OutputDirectory 'dotnet-info.txt')
& git -C $PSScriptRoot rev-parse HEAD | Set-Content (Join-Path $OutputDirectory 'revision.txt')
& git -C $PSScriptRoot status --short | Set-Content (Join-Path $OutputDirectory 'working-tree.txt')
if (Test-Path '/proc/cpuinfo') { Get-Content '/proc/cpuinfo' | Select-Object -First 30 | Set-Content (Join-Path $OutputDirectory 'cpu.txt') }
if (Test-Path '/proc/loadavg') { Get-Content '/proc/loadavg' | Set-Content (Join-Path $OutputDirectory 'load-before.txt') }
$workloads = @(
    @{ Name = 'queue-serial'; Scenario = 'queue'; Producers = 1; Consumers = 1; Subscribers = 1; Payload = 1024; Batch = 1 },
    @{ Name = 'queue-concurrent'; Scenario = 'queue'; Producers = 32; Consumers = 32; Subscribers = 1; Payload = 1024; Batch = 1 },
    @{ Name = 'pubsub-one'; Scenario = 'pubsub'; Producers = 32; Consumers = 32; Subscribers = 1; Payload = 1024; Batch = 1 },
    @{ Name = 'pubsub-four'; Scenario = 'pubsub'; Producers = 32; Consumers = 8; Subscribers = 4; Payload = 1024; Batch = 1 }
)
if ($Profile -eq 'extended') {
    $workloads = @(
        @{ Name = 'queue-16k'; Scenario = 'queue'; Producers = 32; Consumers = 32; Subscribers = 1; Payload = 16384; Batch = 1 },
        @{ Name = 'pubsub-four-16k'; Scenario = 'pubsub'; Producers = 32; Consumers = 8; Subscribers = 4; Payload = 16384; Batch = 1 },
        @{ Name = 'queue-batch10'; Scenario = 'queue'; Producers = 8; Consumers = 32; Subscribers = 1; Payload = 1024; Batch = 10 },
        @{ Name = 'pubsub-four-batch10'; Scenario = 'pubsub'; Producers = 8; Consumers = 8; Subscribers = 4; Payload = 1024; Batch = 10 }
    )
}
if ($Profile -eq 'smoke') { $Seconds = 1; $Warmup = 1; $Repetitions = 1; $workloads = @($workloads[1], $workloads[3]) }
if ($Profile -eq 'soak') { $Seconds = 120; $Warmup = 5; $Repetitions = 1; $workloads = @($workloads[1], $workloads[3]) }
$maxMessages = if ($Profile -eq 'soak') { 100000000 } else { 20000000 }
$cases = foreach ($engine in $Engines) {
    if ($engine -notin @('foundatio-memory', 'masstransit-memory', 'foundatio-redis', 'foundatio-sqs', 'masstransit-sqs')) { throw "Unknown engine $engine" }
    foreach ($workload in $workloads) { [pscustomobject]@{ Engine = $engine; Workload = $workload } }
}
$random = [System.Random]::new(533)
$failures = 0
$index = 0
foreach ($round in 1..$Repetitions) {
    foreach ($case in ($cases | Sort-Object { $random.Next() })) {
        $index++
        $w = $case.Workload
        $parts = $case.Engine.Split('-')
        $name = "round$round-$($case.Engine)-$($w.Name)"
        $startedUtc = [DateTimeOffset]::UtcNow
        Write-Host "[$index/$($cases.Count * $Repetitions)] $name"
        $arguments = @($dll, '--engine', $parts[0], '--transport', $parts[1], '--scenario', $w.Scenario,
            '--seconds', $Seconds, '--warmup', $Warmup, '--producers', $w.Producers, '--consumers', $w.Consumers,
            '--prefetch', $w.Consumers, '--subscribers', $w.Subscribers, '--payload', $w.Payload, '--batch', $w.Batch,
            '--outstanding', 1024, '--max-messages', $maxMessages, '--output', (Join-Path $OutputDirectory "$name.json"))
        try { & $DotnetPath @arguments > (Join-Path $OutputDirectory "$name.log") 2>&1 }
        catch {
            $failures++
            $resultPath = Join-Path $OutputDirectory "$name.json"
            if (-not (Test-Path $resultPath)) {
                $failure = @{
                    Success = $false
                    StartedUtc = $startedUtc
                    Error = "Worker exited without a result. " + ((Get-Content (Join-Path $OutputDirectory "$name.log") -Tail 20) -join "`n")
                    Options = @{ Engine = $parts[0]; Transport = $parts[1]; Scenario = $w.Scenario; DurationSeconds = $Seconds; WarmupSeconds = $Warmup; DrainSeconds = 120; MaxMessages = $maxMessages; ProducerConcurrency = $w.Producers; ConsumerConcurrency = $w.Consumers; Prefetch = $w.Consumers; DeliveryCopies = $w.Subscribers; PayloadBytes = $w.Payload; BatchSize = $w.Batch; RatePerSecond = 0; MaxOutstanding = 1024 }
                }
                $failure | ConvertTo-Json -Depth 5 | Set-Content $resultPath
            }
            Write-Warning "$name failed; retained its log and result."
        }
    }
}
if (Test-Path '/proc/loadavg') { Get-Content '/proc/loadavg' | Set-Content (Join-Path $OutputDirectory 'load-after.txt') }
& (Join-Path $PSScriptRoot 'summarize.ps1') -Directory $OutputDirectory
if ($failures -gt 0) { throw "$failures trials failed. Failed trials are excluded from rankings and listed in the report." }
