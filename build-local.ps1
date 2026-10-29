param([string]$GameRoot)
# Build without NuGet downloads, using the installed .NET 6 runtime as compiler
# metadata references. For ordinary development prefer `dotnet build` with the
# .NET 6 targeting pack installed. This script never deploys into Mods/.
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskGameRoot = if ($GameRoot) { (Resolve-Path -LiteralPath $GameRoot).Path } else { throw 'Specify -GameRoot with the path to your Among Us 3D installation.' }
$taskDotnet = (Get-Command dotnet).Source
$taskDotnetRoot = Split-Path $taskDotnet -Parent
$taskSdk = Get-ChildItem (Join-Path $taskDotnetRoot 'sdk') -Directory |
    Sort-Object { [version]$_.Name.Split('-')[0] } -Descending | Select-Object -First 1
$taskRuntime = Get-ChildItem (Join-Path $taskDotnetRoot 'shared\Microsoft.NETCore.App') -Directory |
    Where-Object Name -Like '6.0.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$taskRuntime) { throw 'Install a .NET 6 runtime or build with the .NET 6 SDK.' }
$taskOut = Join-Path $taskRoot 'bin'
New-Item -ItemType Directory -Path $taskOut -Force | Out-Null
$taskReferences = @(
    (Get-ChildItem (Join-Path $taskRuntime.FullName '*.dll'))
    (Get-Item (Join-Path $taskGameRoot 'MelonLoader\net6\MelonLoader.dll'))
    (Get-Item (Join-Path $taskGameRoot 'MelonLoader\net6\0Harmony.dll'))
    (Get-Item (Join-Path $taskGameRoot 'MelonLoader\net6\Il2CppInterop.Runtime.dll'))
    (Get-ChildItem (Join-Path $taskGameRoot 'MelonLoader\Il2CppAssemblies\*.dll'))
) | ForEach-Object {
    # Runtime directories contain native DLLs too. Reference only CLR assemblies.
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $_.FullName } catch { }
}
$taskResponse = @('/nologo', '/nostdlib+', '/target:library', '/optimize+', '/langversion:latest', '/utf8output',
    ('/out:"' + (Join-Path $taskOut 'LocalCosmeticPreview.dll') + '"'))
$taskResponse += $taskReferences | ForEach-Object { '/reference:"' + $_ + '"' }
$taskResponse += Get-ChildItem (Join-Path $taskRoot 'Source\*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
$taskResponseFile = Join-Path $taskOut 'compile.rsp'
$taskResponse | Set-Content -LiteralPath $taskResponseFile -Encoding utf8
& $taskDotnet (Join-Path $taskSdk.FullName 'Roslyn\bincore\csc.dll') /noconfig ('@' + $taskResponseFile)
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output (Join-Path $taskOut 'LocalCosmeticPreview.dll')
