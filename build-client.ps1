$ErrorActionPreference = 'Stop'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
dotnet build (Join-Path $PSScriptRoot 'Client/Client.csproj') --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
