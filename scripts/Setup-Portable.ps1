# Local Windows toolchain only. No system service, global PATH change or cloud resources.
param([switch]$SkipWebInstall)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
New-Item -ItemType Directory -Force .tools, .local | Out-Null
if (!(Test-Path .tools/dotnet/dotnet.exe)) {
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile .tools/dotnet-install.ps1
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File .tools/dotnet-install.ps1 -Version 10.0.401 -InstallDir .tools/dotnet -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'SDK setup failed' }
}
if (!(Test-Path .tools/pgsql/bin/postgres.exe)) {
    Invoke-WebRequest https://get.enterprisedb.com/postgresql/postgresql-17.11-3-windows-x64-binaries.zip -OutFile .tools/postgresql.zip
    & tar.exe -xf .tools/postgresql.zip -C .tools
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL extraction failed' }
}
if (!(Test-Path .local/pgdata/PG_VERSION)) {
    if (Test-Path .local/pgdata) { throw 'Partial cluster detected. Inspect .local/pgdata before initialization.' }
    $bytes = New-Object byte[] 24
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [IO.File]::WriteAllText((Join-Path $root '.local/pg-password'), [BitConverter]::ToString($bytes).Replace('-', ''))
    & .tools/pgsql/bin/initdb.exe -D .local/pgdata -U postgres --auth=scram-sha-256 --pwfile=.local/pg-password --encoding=UTF8 --locale=C
    if ($LASTEXITCODE -ne 0) { throw 'Cluster initialization failed' }
}
& .tools/pgsql/bin/pg_ctl.exe -D .local/pgdata status
if ($LASTEXITCODE -ne 0) {
    & .tools/pgsql/bin/pg_ctl.exe -D .local/pgdata -l .local/postgres.log -o '-h 127.0.0.1 -p 55432' -w start
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL startup failed' }
}
if (!(Test-Path .local/connections.json)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Initialize-Local.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Database setup failed' }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Migrate-Local.ps1
if ($LASTEXITCODE -ne 0) { throw 'Migration failed' }
if (!$SkipWebInstall) {
    & npm.cmd ci --prefix src/loanapp-web --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Web dependencies failed' }
}
Write-Output 'Local environment ready. Run Api, Mock and Web with scripts/Run-Local.ps1 in separate terminals.'
