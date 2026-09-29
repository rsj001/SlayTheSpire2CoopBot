#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [Parameter(Mandatory = $true)]
    [string]$RitsuRoot,
    [string]$OutputDirectory = "",
    [switch]$DeployToGame
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.local/coopbot-live-kit'
}
$GameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$RitsuRoot = [System.IO.Path]::GetFullPath($RitsuRoot)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

dotnet build (Join-Path $repositoryRoot 'CombatSolver.csproj') -c Release `
    -p:Sts2Dir="$GameRoot" -p:RitsuWorkshopRoot="$RitsuRoot" -p:CopyModOnBuild=false
if ($LASTEXITCODE -ne 0) { throw "CombatSolver Release build failed with exit code $LASTEXITCODE." }

dotnet build (Join-Path $repositoryRoot 'coopbot/CoopBot.csproj') -c Release `
    -p:Sts2Dir="$GameRoot" -p:RitsuWorkshopRoot="$RitsuRoot" -p:CopyModOnBuild=false
if ($LASTEXITCODE -ne 0) { throw "CoopBot Release build failed with exit code $LASTEXITCODE." }

$toolArguments = @($repositoryRoot, $GameRoot, $RitsuRoot, $OutputDirectory)
if ($DeployToGame.IsPresent) { $toolArguments += '--deploy' }
dotnet run --project (Join-Path $repositoryRoot 'tools/CoopBot.LiveKit/CoopBot.LiveKit.csproj') `
    -c Release -- @toolArguments
if ($LASTEXITCODE -ne 0) { throw "CoopBot live-kit preparation failed with exit code $LASTEXITCODE." }
