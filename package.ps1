#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$FFprobeDirectory = (Join-Path $PSScriptRoot '.artifacts\ffprobe-atlas\distribution'),
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

function Get-TaskRelativePath([string]$Root, [string]$Path) {
    $relative = [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)') {
        throw "Path is outside its packaging root: $Path"
    }
    return $relative
}

function Assert-TaskNoLink([IO.FileSystemInfo]$Item) {
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Packaging does not follow symbolic links or junctions: $($Item.FullName)"
    }
}

function Copy-TaskFile([string]$Source, [string]$Destination) {
    Assert-TaskNoLink (Get-Item -LiteralPath $Source -Force)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    [IO.File]::Copy($Source, $Destination, $false)
}

function New-TaskZip([string]$Folder, [string]$ZipPath, [string[]]$DirectoryEntries = @()) {
    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($directory in $DirectoryEntries) { $archive.CreateEntry($directory) | Out-Null }
            foreach ($file in Get-ChildItem -LiteralPath $Folder -File -Recurse -Force | Sort-Object FullName) {
                $entry = $archive.CreateEntry((Get-TaskRelativePath $Folder $file.FullName), [IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [IO.File]::OpenRead($file.FullName)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) }
                    finally { $outputStream.Dispose() }
                }
                finally { $inputStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-TaskZipEntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Assert-TaskReleaseZip([string]$ZipPath, [string]$ExpectedDllHash, [object[]]$ExpectedFiles) {
    $stream = [IO.File]::OpenRead($ZipPath)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            foreach ($name in $names) {
                if ($name.StartsWith('/') -or $name.Contains('\') -or $name -match '(^|/)\.\.(/|$)') { throw "Unsafe ZIP entry: $name" }
                if ($name -match '(?i)(^|/)(ffmpeg\.exe|Unbroken\.LaunchBox\.Plugins\.dll)$') { throw "Forbidden release entry: $name" }
            }
            $roots = @($names | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
            if ($roots.Count -ne 2 -or $roots[0] -cne 'Plugins' -or $roots[1] -cne 'ThirdParty') {
                throw "Release ZIP must contain exactly the Plugins and ThirdParty root directories. Found: $($roots -join ', ')"
            }
            if (-not $archive.GetEntry('Plugins/') -or -not $archive.GetEntry('ThirdParty/')) { throw 'Release root directory entries are missing.' }
            if (-not $archive.GetEntry('ThirdParty/FFMPEG/ffprobe.exe')) { throw 'Release ZIP is missing ffprobe.exe.' }
            if (-not @($names | Where-Object { $_ -clike 'ThirdParty/FFMPEG/FFprobe-Notices/*' }).Count) { throw 'Release ZIP is missing FFprobe-Notices.' }
            $dll = $archive.GetEntry('Plugins/SteamMetadataImporter/SteamMetadataImporter.dll')
            if ($null -eq $dll -or (Get-TaskZipEntryHash $dll) -cne $ExpectedDllHash) { throw 'Packaged plugin DLL does not match the verified Release DLL.' }
            foreach ($expected in $ExpectedFiles) {
                $entry = $archive.GetEntry($expected.Path)
                if ($null -eq $entry -or $entry.Length -ne $expected.Bytes -or (Get-TaskZipEntryHash $entry) -cne $expected.SHA256) {
                    throw "Release entry failed its integrity check: $($expected.Path)"
                }
            }
            if (@($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count -ne $ExpectedFiles.Count + 1) {
                throw 'Unexpected files were found in the release ZIP.'
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-TaskSourceZip([string]$ZipPath, [int]$ExpectedFileCount) {
    $stream = [IO.File]::OpenRead($ZipPath)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $topFiles = @('build.ps1', 'package.ps1', 'README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', '.gitignore')
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName
                if ($name -match '(?i)(^|/)(bin|obj|Data|\.artifacts|dist)(/|$)' -or $name.Contains('\') -or $name -match '(^|/)\.\.(/|$)') {
                    throw "Excluded source entry was packaged: $name"
                }
                if ($topFiles -cnotcontains $name -and $name -cnotmatch '^(src|tests)/.+\.(cs|csproj)$') {
                    throw "Unexpected source entry was packaged: $name"
                }
            }
            if ($archive.Entries.Count -ne $ExpectedFileCount) { throw 'Source ZIP entry count does not match its allowlisted inputs.' }
            foreach ($name in $topFiles) { if (-not $archive.GetEntry($name)) { throw "Source ZIP is missing $name." } }
            if (-not $archive.GetEntry('src/SteamMetadataImporter/SteamMetadataImporter.csproj') -or -not $archive.GetEntry('tests/SteamMetadataImporter.Tests.csproj')) {
                throw 'Source ZIP is missing a required project.'
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

$taskProjectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$taskProjectFile = Join-Path $taskProjectRoot 'src\SteamMetadataImporter\SteamMetadataImporter.csproj'
$taskDll = Join-Path $taskProjectRoot 'src\SteamMetadataImporter\bin\Release\net9.0-windows\SteamMetadataImporter.dll'
$taskDistribution = [IO.Path]::GetFullPath($FFprobeDirectory)
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskTopFiles = @('build.ps1', 'package.ps1', 'README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', '.gitignore')
foreach ($required in @($taskProjectFile, $taskDll, (Join-Path $taskDistribution 'ffprobe.exe')) + @($taskTopFiles | ForEach-Object { Join-Path $taskProjectRoot $_ })) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required package input is missing: $required" }
}
Assert-TaskNoLink (Get-Item -LiteralPath $taskDistribution -Force)
$taskNotices = Join-Path $taskDistribution 'FFprobe-Notices'
if (-not (Test-Path -LiteralPath $taskNotices -PathType Container) -or -not @(Get-ChildItem -LiteralPath $taskNotices -Recurse -File -Force).Count) {
    throw "A nonempty FFprobe-Notices directory is required: $taskNotices"
}
$taskDistributionEntries = @(Get-ChildItem -LiteralPath $taskDistribution -Recurse -Force)
foreach ($entry in $taskDistributionEntries) {
    Assert-TaskNoLink $entry
    if ($entry.Name -ieq 'ffmpeg.exe') { throw 'Do not bundle ffmpeg.exe: this release must preserve the user''s existing FFmpeg executable.' }
    if ($entry.Name -ieq 'Unbroken.LaunchBox.Plugins.dll') { throw 'Do not redistribute the LaunchBox API DLL.' }
}

$taskProjectXml = [xml](Get-Content -LiteralPath $taskProjectFile -Raw)
$taskVersionNode = $taskProjectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $taskVersionNode) { throw 'Project file must contain a concrete Version value.' }
$taskVersion = $taskVersionNode.InnerText.Trim()
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?$') { throw "Unsupported package version: $taskVersion" }
$taskAssembly = [Reflection.AssemblyName]::GetAssemblyName($taskDll)
$taskNumericVersion = [version](($taskVersion -split '[-+]')[0])
$taskExpectedAssemblyVersion = [version]::new($taskNumericVersion.Major, $taskNumericVersion.Minor, $taskNumericVersion.Build, [Math]::Max(0, $taskNumericVersion.Revision))
if ($taskAssembly.Name -cne 'SteamMetadataImporter' -or $taskAssembly.Version -ne $taskExpectedAssemblyVersion) {
    throw "Release DLL identity/version does not match csproj $taskVersion. Rebuild before packaging."
}
$taskDllHash = (Get-FileHash -LiteralPath $taskDll -Algorithm SHA256).Hash.ToLowerInvariant()

$taskStageParent = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '.artifacts'))
$taskStage = [IO.Path]::GetFullPath((Join-Path $taskStageParent ('package-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))))
if ([IO.Path]::GetDirectoryName($taskStage) -ine $taskStageParent) { throw 'Packaging staging directory escaped the project artifacts directory.' }
[IO.Directory]::CreateDirectory($taskStage) | Out-Null
$taskReleaseTree = Join-Path $taskStage 'release'
$taskPluginFolder = Join-Path $taskReleaseTree 'Plugins\SteamMetadataImporter'
$taskMediaFolder = Join-Path $taskReleaseTree 'ThirdParty\FFMPEG'
[IO.Directory]::CreateDirectory($taskPluginFolder) | Out-Null
[IO.Directory]::CreateDirectory($taskMediaFolder) | Out-Null
Copy-TaskFile $taskDll (Join-Path $taskPluginFolder 'SteamMetadataImporter.dll')
foreach ($name in @('README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) { Copy-TaskFile (Join-Path $taskProjectRoot $name) (Join-Path $taskPluginFolder $name) }
foreach ($file in $taskDistributionEntries | Where-Object { -not $_.PSIsContainer }) {
    Copy-TaskFile $file.FullName (Join-Path $taskMediaFolder (Get-TaskRelativePath $taskDistribution $file.FullName))
}
$taskReleaseFiles = @(Get-ChildItem -LiteralPath $taskReleaseTree -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = Get-TaskRelativePath $taskReleaseTree $_.FullName; Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ Version = $taskVersion; CreatedUtc = [DateTime]::UtcNow.ToString('o'); Files = $taskReleaseFiles } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskPluginFolder 'SHA256SUMS.json') -Encoding utf8

$taskSourceTree = Join-Path $taskStage 'source'
[IO.Directory]::CreateDirectory($taskSourceTree) | Out-Null
foreach ($name in $taskTopFiles) { Copy-TaskFile (Join-Path $taskProjectRoot $name) (Join-Path $taskSourceTree $name) }
foreach ($name in @('src', 'tests')) {
    $taskSourceRoot = Join-Path $taskProjectRoot $name
    Assert-TaskNoLink (Get-Item -LiteralPath $taskSourceRoot -Force)
    foreach ($entry in Get-ChildItem -LiteralPath $taskSourceRoot -Recurse -Force) {
        $relative = Get-TaskRelativePath $taskProjectRoot $entry.FullName
        if ($relative -match '(?i)(^|/)(bin|obj|Data|\.artifacts|dist)(/|$)') { continue }
        Assert-TaskNoLink $entry
        if (-not $entry.PSIsContainer -and $entry.Extension -cin @('.cs', '.csproj')) {
            Copy-TaskFile $entry.FullName (Join-Path $taskSourceTree $relative)
        }
    }
}
$taskSourceCount = @(Get-ChildItem -LiteralPath $taskSourceTree -File -Recurse -Force).Count
$taskReleaseZip = Join-Path $taskStage 'release.zip'
$taskSourceZip = Join-Path $taskStage 'source.zip'
New-TaskZip $taskReleaseTree $taskReleaseZip @('Plugins/', 'ThirdParty/')
New-TaskZip $taskSourceTree $taskSourceZip
Assert-TaskReleaseZip $taskReleaseZip $taskDllHash $taskReleaseFiles
Assert-TaskSourceZip $taskSourceZip $taskSourceCount

[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskBaseName = 'ABetterSteamMetadataImporter-v' + $taskVersion
$taskFinalBase = $taskBaseName
while ((Test-Path -LiteralPath (Join-Path $taskOutput ($taskFinalBase + '.zip'))) -or (Test-Path -LiteralPath (Join-Path $taskOutput ($taskFinalBase + '-source.zip')))) {
    $taskFinalBase = $taskBaseName + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}
$taskFinalRelease = Join-Path $taskOutput ($taskFinalBase + '.zip')
$taskFinalSource = Join-Path $taskOutput ($taskFinalBase + '-source.zip')
[IO.File]::Copy($taskReleaseZip, $taskFinalRelease, $false)
[IO.File]::Copy($taskSourceZip, $taskFinalSource, $false)
if ((Get-FileHash -LiteralPath $taskFinalRelease -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskReleaseZip -Algorithm SHA256).Hash -or
    (Get-FileHash -LiteralPath $taskFinalSource -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskSourceZip -Algorithm SHA256).Hash) {
    throw 'A final archive copy failed its SHA256 check.'
}
Write-Output "Release: $taskFinalRelease"
Write-Output "Source: $taskFinalSource"
Write-Output "Plugin SHA256: $taskDllHash"
Write-Output "Verified staging retained: $taskStage"
