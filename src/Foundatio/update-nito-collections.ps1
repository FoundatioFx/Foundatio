# Pull sources
if (Test-Path deque.zip) {
	del deque.zip
}
Invoke-WebRequest https://github.com/StephenCleary/Deque/archive/v1.0.0.zip -OutFile deque.zip
if (Test-Path deque-temp) {
	rmdir '.\deque-temp' -Recurse -Force
}
[System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($pwd.Path + "\deque.zip", $pwd.Path + "\deque-temp")

if (Test-Path Nito.Collections.Deque) {
	rmdir '.\Nito.Collections.Deque' -Recurse -Force
}

cd 'deque-temp\Deque*'
Copy-Item 'Src\Nito.Collections.Deque' -Destination '..\..\' -Recurse
Copy-Item 'LICENSE' -Destination '..\..\Nito.Collections.Deque'
cd '..\..\'

rmdir '.\deque-temp' -Recurse -Force
del deque.zip

Get-ChildItem '.\Nito.Collections.Deque' *.cs -recurse |
    Foreach-Object {
        $c = ($_ | Get-Content) 
        $c = $c -replace 'Nito.Collections','Foundatio.Collections'
        $c | Set-Content $_.FullName
    }

del '.\Nito.Collections.Deque\*.csproj' -Force
del '.\Nito.Collections.Deque\*.xproj' -Force
del '.\Nito.Collections.Deque\*project.json' -Force