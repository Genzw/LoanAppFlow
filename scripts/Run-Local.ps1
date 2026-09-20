param([ValidateSet('Api','Mock','Web')][string]$Component = 'Api')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($Component -eq 'Web') {
    $env:APP_PROFILE = 'LocalDevelopment'
    $env:BACKEND_BASE_URL = 'http://127.0.0.1:5100'
    & npm.cmd --prefix src/loanapp-web run dev
} else {
    $config = Get-Content -Raw .local/connections.json | ConvertFrom-Json
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    if ($Component -eq 'Api') {
        $env:ConnectionStrings__AppDb = $config.AppRuntime
        $env:Outbox__Enabled = 'true'
        $env:Outbox__BaseUrl = 'http://127.0.0.1:5200/'
    } else { $env:ConnectionStrings__MockDb = $config.MockRuntime }
    $port = if ($Component -eq 'Api') { 5100 } else { 5200 }
    & .tools/dotnet/dotnet.exe run --no-launch-profile --project "src/LoanApp.$Component" -- --urls "http://127.0.0.1:$port"
}
exit $LASTEXITCODE
