param([switch]$Browser, [switch]$MigrationBundles)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$config = Get-Content -Raw .local/connections.json | ConvertFrom-Json
$env:DOTNET_ROOT = Join-Path $root '.tools/dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$psql = Join-Path $root '.tools/pgsql/bin/psql.exe'
$suffix = [Guid]::NewGuid().ToString('N')
$testDatabases = @("loanapp_test_$suffix", "loanapp_mock_test_$suffix")
function Run-Sql([string]$user, [string]$database, [string]$sql) {
    & $psql -h 127.0.0.1 -p $config.Port -U $user -d $database -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw 'Test database operation failed' }
}
try {
    if ($MigrationBundles) {
        & (Join-Path $PSScriptRoot 'Build-MigrationBundles.ps1') -Runtime win-x64
        $bundleDirectory = Join-Path $root '.local/migrations/win-x64'
        $manifest = Get-Content -LiteralPath (Join-Path $bundleDirectory 'manifest.json') -Raw | ConvertFrom-Json
        foreach ($component in @('Api','Mock')) {
            $name = 'migrate-' + $component.ToLowerInvariant() + '.exe'
            $entry = @($manifest.artifacts | Where-Object { $_.component -eq $component -and $_.file -eq $name })
            if ($entry.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $bundleDirectory $name) -Algorithm SHA256).Hash -ne $entry[0].sha256) {
                throw "Bundle manifest verification failed: $component"
            }
        }
    }
    for ($i = 0; $i -lt 2; $i++) {
        $dbName = $testDatabases[$i]
        $owner = if ($i -eq 0) { 'loanapp_migrator' } else { 'loanapp_mock_migrator' }
        $runtime = if ($i -eq 0) { 'loanapp_runtime' } else { 'loanapp_mock_runtime' }
        $env:PGPASSWORD = [IO.File]::ReadAllText((Join-Path $root '.local/pg-password'))
        Run-Sql postgres postgres "CREATE DATABASE $dbName OWNER $owner"
        Run-Sql postgres postgres "REVOKE ALL ON DATABASE $dbName FROM PUBLIC; GRANT CONNECT ON DATABASE $dbName TO $runtime;"
        Run-Sql postgres $dbName "REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO $runtime;"
        $migration = if ($i -eq 0) { $config.AppMigration } else { $config.MockMigration }
        $runtimeConnection = if ($i -eq 0) { $config.AppRuntime } else { $config.MockRuntime }
        $migration = $migration -replace 'Database=[^;]+', "Database=$dbName"
        $runtimeConnection = $runtimeConnection -replace 'Database=[^;]+', "Database=$dbName"
        if ($i -eq 0) { $env:ConnectionStrings__AppDb = $migration; $env:TEST_APP_DB = $runtimeConnection; $env:TEST_APP_MIGRATION_DB = $migration }
        else { $env:ConnectionStrings__MockDb = $migration; $env:TEST_MOCK_DB = $runtimeConnection; $env:TEST_MOCK_MIGRATION_DB = $migration }
        $component = if ($i -eq 0) { 'Api' } else { 'Mock' }
        if ($MigrationBundles) {
            $bundle = Join-Path $root ('.local/migrations/win-x64/migrate-' + $component.ToLowerInvariant() + '.exe')
            foreach ($pass in 1..2) {
                & $bundle
                if ($LASTEXITCODE -ne 0) { throw "Test bundle migration failed: $component, pass $pass" }
            }
        } else {
            & .tools/dotnet/dotnet.exe tool run dotnet-ef database update --project "src/LoanApp.$component"
            if ($LASTEXITCODE -ne 0) { throw 'Test migration failed' }
        }
        $env:PGPASSWORD = (($migration -split ';' | Where-Object { $_ -like 'Password=*' }) -replace '^Password=', '')
        & $psql -h 127.0.0.1 -p $config.Port -U $owner -d $dbName -v ON_ERROR_STOP=1 -v "runtime_role=$runtime" -f infra/grant-runtime.sql
        if ($LASTEXITCODE -ne 0) { throw 'Test grants failed' }
    }
    $env:Outbox__Enabled = 'false'
    & .tools/dotnet/dotnet.exe test LoanAppFlow.sln --logger 'trx;LogFileName=tests.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    if ($Browser) {
        $env:ConnectionStrings__AppDb = $env:TEST_APP_DB
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:LocalWebOrigin = 'http://127.0.0.1:3300'
        $apiDll = Join-Path $root 'src/LoanApp.Api/bin/Debug/net10.0/LoanApp.Api.dll'
        & .tools/dotnet/dotnet.exe $apiDll --seed-policy (Join-Path $root 'config/initial-policy.json')
        if ($LASTEXITCODE -ne 0) { throw 'Browser seed failed' }
        $env:ConnectionStrings__MockDb = $env:TEST_MOCK_DB
        $mockDll = Join-Path $root 'src/LoanApp.Mock/bin/Debug/net10.0/LoanApp.Mock.dll'
        $mockProcess = Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @(('"' + $mockDll + '"'), '--urls', 'http://127.0.0.1:5351') -PassThru -WindowStyle Hidden -RedirectStandardOutput .local/e2e-mock.log -RedirectStandardError .local/e2e-mock-error.log
        Remove-Item Env:ConnectionStrings__MockDb -ErrorAction SilentlyContinue
        $env:Outbox__Enabled = 'true'
        $env:Outbox__BaseUrl = 'http://127.0.0.1:5351/'
        $apiProcess = Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @(('"' + $apiDll + '"'), '--urls', 'http://127.0.0.1:5350') -PassThru -WindowStyle Hidden -RedirectStandardOutput .local/e2e-api.log -RedirectStandardError .local/e2e-api-error.log
        Remove-Item Env:ConnectionStrings__AppDb, Env:ConnectionStrings__MockDb, Env:PGPASSWORD, Env:TEST_APP_DB, Env:TEST_MOCK_DB, Env:TEST_APP_MIGRATION_DB, Env:TEST_MOCK_MIGRATION_DB, Env:Outbox__Enabled, Env:Outbox__BaseUrl -ErrorAction SilentlyContinue
        $env:APP_PROFILE = 'LocalDevelopment'
        $env:BACKEND_BASE_URL = 'http://127.0.0.1:5350'
        $env:NEXT_DIST_DIR = '.next-e2e'
        $env:TEST_E2E_BASE_URL = 'http://127.0.0.1:3300'
        $env:TEST_E2E_MOCK_URL = 'http://127.0.0.1:5351'
        $env:LOCAL_WEB_ORIGIN = 'http://127.0.0.1:3300'
        $nextCli = Join-Path $root 'src/loanapp-web/node_modules/next/dist/bin/next'
        $webProcess = Start-Process -FilePath (Get-Command node.exe).Source -WorkingDirectory (Join-Path $root 'src/loanapp-web') -ArgumentList @(('"' + $nextCli + '"'), 'dev', '--hostname', '127.0.0.1', '--port', '3300') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root '.local/e2e-web.log') -RedirectStandardError (Join-Path $root '.local/e2e-web-error.log')
        $ready = $false
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            try {
                $response = Invoke-WebRequest http://127.0.0.1:3300/api/admin/policy -UseBasicParsing -TimeoutSec 5
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (!$ready) { throw 'Browser stack did not become ready. Inspect .local/e2e-*.log.' }
        & npm.cmd --prefix tests/e2e test
        if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed' }
    }
} finally {
    foreach ($process in @($apiProcess, $webProcess, $mockProcess)) {
        if ($null -ne $process -and !$process.HasExited) { & taskkill.exe /PID $process.Id /T /F | Out-Null }
    }
    $env:PGPASSWORD = [IO.File]::ReadAllText((Join-Path $root '.local/pg-password'))
    foreach ($dbName in $testDatabases) {
        if ($dbName -notmatch '^loanapp_(mock_)?test_[a-f0-9]{32}$') { throw 'Unexpected test database name; refusing cleanup' }
        & $psql -h 127.0.0.1 -p $config.Port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $dbName WITH (FORCE)"
    }
    Remove-Item Env:TEST_APP_DB, Env:TEST_MOCK_DB, Env:TEST_APP_MIGRATION_DB, Env:TEST_MOCK_MIGRATION_DB, Env:TEST_E2E_MOCK_URL, Env:Outbox__Enabled, Env:Outbox__BaseUrl, Env:PGPASSWORD, Env:ConnectionStrings__AppDb, Env:ConnectionStrings__MockDb -ErrorAction SilentlyContinue
    Remove-Item Env:LocalWebOrigin, Env:LOCAL_WEB_ORIGIN, Env:APP_PROFILE, Env:BACKEND_BASE_URL, Env:NEXT_DIST_DIR, Env:TEST_E2E_BASE_URL -ErrorAction SilentlyContinue
}
