#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Setup Cursor Rules

.DESCRIPTION
    Copies GitHub instruction files from .github/instructions/*.md
    to .cursor/rules/*.mdc for use with Cursor IDE.
#>

function Setup-CursorRules {
    Write-Host "Setting up Cursor rules..." -ForegroundColor Green

    # Go up one level from .cursor directory to repository root
    $repoRoot = Split-Path -Parent $PSScriptRoot

    $srcDir = Join-Path $repoRoot ".github" "instructions"
    $destDir = Join-Path $repoRoot ".cursor" "rules"

    # Ensure destination directory exists
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        Write-Host "Created .cursor/rules directory" -ForegroundColor Yellow
    }

    # Check if source directory exists
    if (-not (Test-Path $srcDir)) {
        Write-Warning "Warning: .github/instructions directory not found"
        return
    }

    # Get all .md files
    $files = Get-ChildItem -Path $srcDir -Filter "*.md" -File

    if ($files.Count -eq 0) {
        Write-Host "No .md files found in .github/instructions" -ForegroundColor Yellow
        return
    }

    $copiedCount = 0

    foreach ($file in $files) {
        $srcPath = $file.FullName
        $destFileName = $file.BaseName + ".mdc"
        $destPath = Join-Path $destDir $destFileName

        try {
            Copy-Item -Path $srcPath -Destination $destPath -Force
            Write-Host "Copied $($file.Name) to .cursor/rules/$destFileName" -ForegroundColor Cyan
            $copiedCount++
        }
        catch {
            Write-Error "Error copying $($file.Name): $($_.Exception.Message)"
        }
    }

    Write-Host "Successfully copied $copiedCount instruction file(s) to Cursor rules." -ForegroundColor Green
}

# Run the script
Setup-CursorRules
