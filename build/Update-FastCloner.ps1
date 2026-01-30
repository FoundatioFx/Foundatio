$work_dir = Resolve-Path "$PSScriptRoot"
$src_dir = Resolve-Path "$PSScriptRoot/../src/Foundatio"

# Standard using directives and nullable enable to prepend to files
$standardUsings = @"
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

"@

Function UpdateFastCloner {
    param([string]$version)

    $sourceUrl = "https://github.com/lofcz/FastCloner/archive/refs/tags/v$version.zip"
    $name = "FastCloner"

    $zipPath = Join-Path $work_dir "$name.zip"
    $extractPath = Join-Path $work_dir $name
    $destPath = Join-Path $src_dir $name

    # Download and extract
    If (Test-Path $zipPath) { Remove-Item $zipPath }
    Invoke-WebRequest $sourceUrl -OutFile $zipPath

    If (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $extractPath
    Remove-Item $zipPath

    # Clean destination
    If (Test-Path $destPath) { Remove-Item $destPath -Recurse -Force }

    $dir = (Get-ChildItem $extractPath | Select-Object -First 1).FullName

    # Create directory structure
    New-Item $destPath -Type Directory | Out-Null
    New-Item (Join-Path $destPath "Code") -Type Directory | Out-Null

    # Copy LICENSE
    Copy-Item (Join-Path $dir "LICENSE") -Destination $destPath -Force

    # Copy and transform FastCloner.cs (skip FastClonerExtensions.cs - we have our own)
    $srcFastClonerPath = Join-Path $dir "src/FastCloner"
    Get-ChildItem -Path $srcFastClonerPath -Filter "FastCloner.cs" |
        Foreach-Object {
            $c = ($_ | Get-Content -Raw)
            $c = $c -replace 'namespace FastCloner;','namespace Foundatio.FastCloner;'
            $c = $c -replace 'using FastCloner\.Code;','using Foundatio.FastCloner.Code;'
            # Remove existing using directives that we're adding
            $c = $c -replace 'using System;[\r\n]+', ''
            $c = $c -replace 'using System\.Collections\.Generic;[\r\n]+', ''
            $c = $c -replace 'using System\.Collections\.Concurrent;[\r\n]+', ''
            $c = $c -replace 'using System\.Linq;[\r\n]+', ''
            $c = $c -replace 'using System\.Linq\.Expressions;[\r\n]+', ''
            $c = $c -replace 'using System\.Reflection;[\r\n]+', ''
            $c = $c -replace 'using System\.Runtime\.CompilerServices;[\r\n]+', ''
            $c = $c -replace 'using System\.Threading;[\r\n]+', ''
            # Make all public types internal (we only expose via ObjectExtensions.DeepClone)
            $c = $c -replace 'public static class','internal static class'
            $c = $c -replace 'public class','internal class'
            $c = $c -replace 'public enum','internal enum'
            $c = $c -replace 'public struct','internal struct'
            # Replace MODERN preprocessor directives with always-true/false for .NET 8+
            # Foundatio only targets net8.0 and net10.0, so MODERN is always true
            $c = $c -replace '#if MODERN','#if true // MODERN'
            $c = $c -replace '#if !MODERN','#if false // !MODERN'
            $c = $c -replace '#elif MODERN','#elif true // MODERN'
            $c = $c -replace '#elif !MODERN','#elif false // !MODERN'
            # Add standard usings at the top
            $c = $standardUsings + $c
            $c | Set-Content (Join-Path $destPath $_.Name)
        }

    # Copy and transform Code/*.cs files
    $srcCodePath = Join-Path $dir "src/FastCloner/Code"
    $destCodePath = Join-Path $destPath "Code"
    Get-ChildItem -Path $srcCodePath -Filter *.cs |
        Foreach-Object {
            $c = ($_ | Get-Content -Raw)
            $c = $c -replace 'namespace FastCloner\.Code;','namespace Foundatio.FastCloner.Code;'
            $c = $c -replace 'namespace FastCloner;','namespace Foundatio.FastCloner;'
            $c = $c -replace 'using FastCloner\.Code;','using Foundatio.FastCloner.Code;'
            $c = $c -replace 'using FastCloner;','using Foundatio.FastCloner;'
            # Remove existing using directives that we're adding
            $c = $c -replace 'using System;[\r\n]+', ''
            $c = $c -replace 'using System\.Collections\.Generic;[\r\n]+', ''
            $c = $c -replace 'using System\.Collections\.Concurrent;[\r\n]+', ''
            $c = $c -replace 'using System\.Linq;[\r\n]+', ''
            $c = $c -replace 'using System\.Linq\.Expressions;[\r\n]+', ''
            $c = $c -replace 'using System\.Reflection;[\r\n]+', ''
            $c = $c -replace 'using System\.Runtime\.CompilerServices;[\r\n]+', ''
            $c = $c -replace 'using System\.Threading;[\r\n]+', ''
            # Make all public types internal (we only expose via ObjectExtensions.DeepClone)
            $c = $c -replace 'public static class','internal static class'
            $c = $c -replace 'public class','internal class'
            $c = $c -replace 'public enum','internal enum'
            $c = $c -replace 'public struct','internal struct'
            # Replace MODERN preprocessor directives with always-true/false for .NET 8+
            # Foundatio only targets net8.0 and net10.0, so MODERN is always true
            $c = $c -replace '#if MODERN','#if true // MODERN'
            $c = $c -replace '#if !MODERN','#if false // !MODERN'
            $c = $c -replace '#elif MODERN','#elif true // MODERN'
            $c = $c -replace '#elif !MODERN','#elif false // !MODERN'
            # Add standard usings at the top
            $c = $standardUsings + $c
            $c | Set-Content (Join-Path $destCodePath $_.Name)
        }

    # Cleanup
    Remove-Item $extractPath -Recurse -Force
}

UpdateFastCloner "3.4.4"
