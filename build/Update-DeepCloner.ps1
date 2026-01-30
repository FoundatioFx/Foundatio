$work_dir = Resolve-Path "$PSScriptRoot"
$src_dir = Resolve-Path "$PSScriptRoot/../src/Foundatio"

Function UpdateDeepCloner {
    param([string]$version)

    $sourceUrl = "https://github.com/force-net/DeepCloner/archive/refs/tags/v$version.zip"
    $name = "DeepCloner"

    $zipPath = Join-Path $work_dir "$name.zip"
    $extractPath = Join-Path $work_dir $name
    $destPath = Join-Path $src_dir $name

    # Download and extract
    If (Test-Path $zipPath) { Remove-Item $zipPath }
    Write-Host "Downloading DeepCloner v$version..."
    Invoke-WebRequest $sourceUrl -OutFile $zipPath

    If (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $extractPath
    Remove-Item $zipPath

    # Clean destination
    If (Test-Path $destPath) { Remove-Item $destPath -Recurse -Force }

    $dir = (Get-ChildItem $extractPath | Select-Object -First 1).FullName

    # Create directory structure
    New-Item $destPath -Type Directory | Out-Null
    New-Item (Join-Path $destPath "Helpers") -Type Directory | Out-Null

    # Copy LICENSE
    Copy-Item (Join-Path $dir "LICENSE") -Destination $destPath -Force

    # Files to skip (we don't need MSIL generator for .NET Core, and TypeCreationHelper is only used by MSIL)
    $skipFiles = @(
        "DeepClonerMsilGenerator.cs",
        "DeepClonerMsilHelper.cs",
        "TypeCreationHelper.cs"
    )

    # Copy and transform DeepClonerExtensions.cs
    $srcDeepClonerPath = Join-Path $dir "DeepCloner"
    Get-ChildItem -Path $srcDeepClonerPath -Filter "DeepClonerExtensions.cs" |
        Foreach-Object {
            $c = ($_ | Get-Content -Raw)
            # Add NETCORE define at the top
            $c = "#define NETCORE`r`n" + $c
            # Transform namespaces
            $c = $c -replace 'namespace Force\.DeepCloner;','namespace Foundatio.Force.DeepCloner;'
            $c = $c -replace 'namespace Force\.DeepCloner\b','namespace Foundatio.Force.DeepCloner'
            $c = $c -replace 'using Force\.DeepCloner\.Helpers;','using Foundatio.Force.DeepCloner.Helpers;'
            $c = $c -replace 'using Force\.DeepCloner;','using Foundatio.Force.DeepCloner;'
            # Make all public types internal (we only expose via ObjectExtensions.DeepClone)
            $c = $c -replace 'public static class','internal static class'
            $c = $c -replace 'public class','internal class'
            $c = $c -replace 'public enum','internal enum'
            $c = $c -replace 'public struct','internal struct'
            $c = $c -replace 'public abstract class','internal abstract class'
            $c | Set-Content (Join-Path $destPath $_.Name)
            Write-Host "  Processed: $($_.Name)"
        }

    # Copy and transform Helpers/*.cs files
    $srcHelpersPath = Join-Path $dir "DeepCloner/Helpers"
    $destHelpersPath = Join-Path $destPath "Helpers"
    Get-ChildItem -Path $srcHelpersPath -Filter *.cs |
        Where-Object { $skipFiles -notcontains $_.Name } |
        Foreach-Object {
            $c = ($_ | Get-Content -Raw)
            # Add NETCORE define at the top
            $c = "#define NETCORE`r`n" + $c
            # Transform namespaces
            $c = $c -replace 'namespace Force\.DeepCloner\.Helpers;','namespace Foundatio.Force.DeepCloner.Helpers;'
            $c = $c -replace 'namespace Force\.DeepCloner\.Helpers\b','namespace Foundatio.Force.DeepCloner.Helpers'
            $c = $c -replace 'namespace Force\.DeepCloner;','namespace Foundatio.Force.DeepCloner;'
            $c = $c -replace 'namespace Force\.DeepCloner\b','namespace Foundatio.Force.DeepCloner'
            $c = $c -replace 'using Force\.DeepCloner\.Helpers;','using Foundatio.Force.DeepCloner.Helpers;'
            $c = $c -replace 'using Force\.DeepCloner;','using Foundatio.Force.DeepCloner;'
            # Make all public types internal (we only expose via ObjectExtensions.DeepClone)
            $c = $c -replace 'public static class','internal static class'
            $c = $c -replace 'public class','internal class'
            $c = $c -replace 'public enum','internal enum'
            $c = $c -replace 'public struct','internal struct'
            $c = $c -replace 'public abstract class','internal abstract class'
            $c | Set-Content (Join-Path $destHelpersPath $_.Name)
            Write-Host "  Processed: Helpers/$($_.Name)"
        }

    # Cleanup
    Remove-Item $extractPath -Recurse -Force

    Write-Host ""
    Write-Host "DeepCloner v$version updated successfully!"
    Write-Host ""
    Write-Host "Files skipped (not needed for .NET Core):"
    foreach ($skip in $skipFiles) {
        Write-Host "  - $skip"
    }
}

# Update to latest version (0.10.4 as of 2022-04-29)
UpdateDeepCloner "0.10.4"
