$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'src\IoFtp.Desktop\IoFtp.Desktop.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "FluxFTP project file was not found at: $projectPath"
}

dotnet run --project $projectPath
exit $LASTEXITCODE
