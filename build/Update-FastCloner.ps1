$work_dir = Resolve-Path "$PSScriptRoot"
$src_dir = Resolve-Path "$PSScriptRoot/../src/Foundatio"

# Change this to update the FastCloner tag being imported.
$version = "3.5.2"

Function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $false)][string]$WorkingDirectory = $work_dir
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

Function UpdateFastCloner {
    param([string]$version = $script:version)

    $name = "FastCloner"
    $repoUrl = "https://github.com/lofcz/FastCloner.git"
    $tag = "v$version"
    $tempRoot = Join-Path $work_dir ".update-fastcloner"
    $clonePath = Join-Path $tempRoot "repo"
    $stagingPath = Join-Path $tempRoot "output"
    $destPath = Join-Path $src_dir $name
    $cloneSrcPath = Join-Path $clonePath "src"
    $inputRoot = Join-Path $cloneSrcPath $name
    $builderProject = "FastCloner.Internalization.Builder/FastCloner.Internalization.Builder.csproj"

    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force
    }

    New-Item $tempRoot -ItemType Directory | Out-Null

    try {
        Invoke-CheckedCommand git @("clone", "--branch", $tag, "--depth", "1", $repoUrl, $clonePath)

        Invoke-CheckedCommand dotnet @("build", $builderProject) $cloneSrcPath
        Invoke-CheckedCommand dotnet @(
            "run",
            "--project", $builderProject,
            "--",
            "--input-root", $inputRoot,
            "--root-namespace", "Foundatio.FastCloner",
            "--output", $stagingPath,
            "--preprocessor", "MODERN=true;NET5_0_OR_GREATER=true;NET6_0_OR_GREATER=true;NET8_0_OR_GREATER=true",
            "--visibility", "internal",
            "--public-api", "none",
            "--runtime-only", "true",
            "--self-check"
        ) $cloneSrcPath

        if (Test-Path $destPath) {
            Remove-Item $destPath -Recurse -Force
        }

        Move-Item $stagingPath $destPath
    }
    finally {
        if (Test-Path $tempRoot) {
            Remove-Item $tempRoot -Recurse -Force
        }
    }
}

UpdateFastCloner $version
