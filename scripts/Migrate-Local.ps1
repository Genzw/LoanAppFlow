$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
$env:DOTNET_ROOT = Join-Path $root '.tools/dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$config = Get-Content -Raw .local/connections.json | ConvertFrom-Json
$psql = Join-Path $root '.tools/pgsql/bin/psql.exe'
try {
    $env:ConnectionStrings__AppDb = $config.AppMigration
    $env:ConnectionStrings__MockDb = $config.MockMigration
    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed' }
    foreach ($component in @('Api','Mock')) {
        & $dotnet tool run dotnet-ef database update --project "src/LoanApp.$component"
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $component" }
        $connection = if ($component -eq 'Api') { $config.AppMigration } else { $config.MockMigration }
        $parts = @{}
        foreach ($part in $connection.Split(';')) { $key,$value = $part.Split('=',2); $parts[$key]=$value }
        $env:PGPASSWORD = $parts.Password
        $role = if ($component -eq 'Api') { 'loanapp_runtime' } else { 'loanapp_mock_runtime' }
        & $psql -h 127.0.0.1 -p $config.Port -U $parts.Username -d $parts.Database -v ON_ERROR_STOP=1 -v "runtime_role=$role" -f infra/grant-runtime.sql
        if ($LASTEXITCODE -ne 0) { throw "Grants failed: $component" }
    }
} finally {
    Remove-Item Env:PGPASSWORD, Env:ConnectionStrings__AppDb, Env:ConnectionStrings__MockDb -ErrorAction SilentlyContinue
}
