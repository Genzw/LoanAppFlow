$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$config = Get-Content -Raw .local/connections.json | ConvertFrom-Json
$env:ConnectionStrings__AppDb = $config.AppRuntime
$env:ASPNETCORE_ENVIRONMENT = 'Development'
try {
    $policyFile = Join-Path $root 'config/initial-policy.json'
    & .tools/dotnet/dotnet.exe run --no-launch-profile --project src/LoanApp.Api -- --seed-policy $policyFile
    if ($LASTEXITCODE -ne 0) { throw 'Policy initialization failed' }
} finally { Remove-Item Env:ConnectionStrings__AppDb -ErrorAction SilentlyContinue }
