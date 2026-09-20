param([int]$Port = 55432)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$psql = Join-Path $root '.tools/pgsql/bin/psql.exe'
$configFile = Join-Path $root '.local/connections.json'
if (Test-Path -LiteralPath $configFile) { throw 'Local credentials already exist; initialization will not overwrite existing databases.' }
if (!(Test-Path -LiteralPath $psql)) { throw 'Portable PostgreSQL required in .tools/pgsql. See README.' }
function New-LocalPassword {
    $bytes = New-Object byte[] 24
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}
$env:PGPASSWORD = [IO.File]::ReadAllText((Join-Path $root '.local/pg-password'))
$env:API_MIGRATOR_PASSWORD = New-LocalPassword
$env:API_RUNTIME_PASSWORD = New-LocalPassword
$env:MOCK_MIGRATOR_PASSWORD = New-LocalPassword
$env:MOCK_RUNTIME_PASSWORD = New-LocalPassword
try {
    & $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -f infra/init-databases.sql
    if ($LASTEXITCODE -ne 0) { throw 'Database initialization failed. Inspect database state before retrying.' }
    $config = @{
        Port = $Port
        AppMigration = "Host=127.0.0.1;Port=$Port;Database=loanapp;Username=loanapp_migrator;Password=$env:API_MIGRATOR_PASSWORD"
        AppRuntime = "Host=127.0.0.1;Port=$Port;Database=loanapp;Username=loanapp_runtime;Password=$env:API_RUNTIME_PASSWORD;Maximum Pool Size=5;Minimum Pool Size=0"
        MockMigration = "Host=127.0.0.1;Port=$Port;Database=loanapp_mock;Username=loanapp_mock_migrator;Password=$env:MOCK_MIGRATOR_PASSWORD"
        MockRuntime = "Host=127.0.0.1;Port=$Port;Database=loanapp_mock;Username=loanapp_mock_runtime;Password=$env:MOCK_RUNTIME_PASSWORD;Maximum Pool Size=5;Minimum Pool Size=0"
    }
    $config | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $configFile
    Write-Output 'Local databases and separate roles created. Credentials saved in ignored .local/connections.json.'
} finally {
    Remove-Item Env:PGPASSWORD, Env:API_MIGRATOR_PASSWORD, Env:API_RUNTIME_PASSWORD, Env:MOCK_MIGRATOR_PASSWORD, Env:MOCK_RUNTIME_PASSWORD -ErrorAction SilentlyContinue
}
