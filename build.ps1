[CmdletBinding()]
param(
    [string]$LaunchBoxRoot = $env:LAUNCHBOX_ROOT,
    [string]$DotNetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LaunchBoxRoot)) {
    throw 'Supply -LaunchBoxRoot or set LAUNCHBOX_ROOT to the folder containing LaunchBox.exe.'
}
$taskApiPath = Join-Path $LaunchBoxRoot 'Core\Unbroken.LaunchBox.Plugins.dll'
if (-not (Test-Path -LiteralPath $taskApiPath -PathType Leaf)) {
    throw "LaunchBox plugin API not found at $taskApiPath. Supply -LaunchBoxRoot."
}
$LaunchBoxRoot = (Resolve-Path -LiteralPath $LaunchBoxRoot).ProviderPath
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    throw 'Supply -DotNetPath to a .NET 9 SDK executable, or use dotnet from PATH.'
}
if (Test-Path -LiteralPath $DotNetPath -PathType Leaf) {
    $DotNetPath = (Get-Item -LiteralPath $DotNetPath).FullName
}
else {
    $taskDotNetCommand = Get-Command -Name $DotNetPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $taskDotNetCommand) {
        throw "The .NET SDK executable '$DotNetPath' was not found. Install a .NET 9 SDK on PATH or supply -DotNetPath."
    }
    $DotNetPath = $taskDotNetCommand.Path
}

$taskArtifacts = Join-Path $PSScriptRoot '.artifacts'
$taskEmptyFeed = Join-Path $taskArtifacts 'empty-feed'
$taskCliHome = Join-Path $taskArtifacts 'dotnet-home'
$taskNugetCache = Join-Path $taskArtifacts 'nuget'
New-Item -ItemType Directory -Path $taskEmptyFeed, $taskCliHome, $taskNugetCache -Force | Out-Null
$taskSavedEnvironment = @{}
foreach ($taskName in @('DOTNET_CLI_HOME', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TO_PATH', 'DOTNET_NOLOGO', 'NUGET_PACKAGES')) {
    $taskSavedEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
}
try {
    $env:DOTNET_CLI_HOME = $taskCliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TO_PATH = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = $taskNugetCache
    $taskProject = Join-Path $PSScriptRoot 'src\SteamMetadataImporter\SteamMetadataImporter.csproj'
    $taskTests = Join-Path $PSScriptRoot 'tests\SteamMetadataImporter.Tests.csproj'
    $taskRestoreTarget = if ($SkipTests) { $taskProject } else { $taskTests }
    & $DotNetPath restore $taskRestoreTarget "-p:LaunchBoxRoot=$LaunchBoxRoot" --source $taskEmptyFeed --nologo
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
    & $DotNetPath build $taskRestoreTarget -c $Configuration "-p:LaunchBoxRoot=$LaunchBoxRoot" --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    if (-not $SkipTests) {
        $taskTestDll = Join-Path $PSScriptRoot "tests\bin\$Configuration\net9.0-windows\SteamMetadataImporter.Tests.dll"
        & $DotNetPath $taskTestDll
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    }
    $taskPluginDll = Join-Path $PSScriptRoot "src\SteamMetadataImporter\bin\$Configuration\net9.0-windows\SteamMetadataImporter.dll"
    Write-Output "Built: $taskPluginDll"
}
finally {
    foreach ($taskName in $taskSavedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($taskName, $taskSavedEnvironment[$taskName], 'Process')
    }
}
