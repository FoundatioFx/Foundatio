$parentDirectory = Split-Path -Path $PSScriptRoot -Parent
$directories = Get-ChildItem -Path $parentDirectory -Directory -Filter "Foundatio.*"

foreach ($directory in $directories) {
    $composeFilePath = Join-Path -Path $directory.FullName -ChildPath "docker-compose.yml"
    if (Test-Path -Path $composeFilePath) {
        Set-Location -Path $directory.FullName
        Write-Host "Stopping services in directory: $($directory.FullName)"
        docker compose -f $composeFilePath down --remove-orphans
    }
}

Write-Host "Stopped services"
Set-Location -Path $PSScriptRoot
