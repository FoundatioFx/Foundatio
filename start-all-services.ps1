$env:HOST_IP = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
Where-Object {
    $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork -and
    $_.IPAddressToString -notin '127.0.0.1'
} |
Select-Object -First 1 -ExpandProperty IPAddressToString

Write-Host "Using machine IP: $env:HOST_IP"
Write-Host "Starting services in directory: $PSScriptRoot"
docker compose -f (Join-Path -Path $PSScriptRoot -ChildPath "docker-compose.yml") up --detach

# These providers now live in this repo; skip old clones of them next to it.
$inRepoProviders = @("Foundatio.Redis", "Foundatio.AWS", "Foundatio.Kafka")
$parentDirectory = Split-Path -Path $PSScriptRoot -Parent
$directories = Get-ChildItem -Path $parentDirectory -Directory -Filter "Foundatio.*" | Where-Object { $_.Name -notin $inRepoProviders }

foreach ($directory in $directories) {
    $startScriptPath = Join-Path -Path $directory.FullName -ChildPath "start-services.ps1"
    $composeFilePath = Join-Path -Path $directory.FullName -ChildPath "docker-compose.yml"

    if (Test-Path -Path $startScriptPath) {
        Set-Location -Path $directory.FullName
        Write-Host "Starting services in directory using start-services.ps1: $($directory.FullName)"
        & $startScriptPath
    }
    elseif (Test-Path -Path $composeFilePath) {
        Set-Location -Path $directory.FullName
        Write-Host "Starting services in directory: $($directory.FullName)"
        docker compose -f $composeFilePath up --detach
    }
}

Write-Host "Services started successfully."
Set-Location -Path $PSScriptRoot
