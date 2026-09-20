param([ValidateSet('win-x64','linux-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$saved = @{}
foreach ($key in @('ConnectionStrings__AppDb','ConnectionStrings__MockDb','DOTNET_ROOT','PATH')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
Push-Location $root
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    Remove-Item Env:ConnectionStrings__AppDb, Env:ConnectionStrings__MockDb -ErrorAction SilentlyContinue
    $output = Join-Path $root ".local/migrations/$Runtime"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # A failed build must never leave a previous success manifest.
    Remove-Item -LiteralPath (Join-Path $output 'manifest.json') -ErrorAction SilentlyContinue
    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Migration tool restore failed' }
    $artifacts = @()
    foreach ($component in @('Api','Mock')) {
        $project = "src/LoanApp.$component/LoanApp.$component.csproj"
        & $dotnet restore $project --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Locked restore failed: $component" }
        & $dotnet build $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Migration build failed: $component" }
        $extension = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
        $file = Join-Path $output ("migrate-" + $component.ToLowerInvariant() + $extension)
        # EF's temporary RID publish also rewrites referenced projects' locks.
        # Preserve the reviewed, RID-neutral application locks even on failure.
        $locks = @{}
        foreach ($name in @('Core', $component)) {
            $lock = Join-Path $root "src/LoanApp.$name/packages.lock.json"
            $locks[$lock] = [IO.File]::ReadAllBytes($lock)
        }
        try {
            & $dotnet tool run dotnet-ef migrations bundle --project $project --configuration Release --no-build --target-runtime $Runtime --output $file --force
            if ($LASTEXITCODE -ne 0) { throw "Bundle generation failed: $component" }
        } finally {
            foreach ($lock in $locks.Keys) { [IO.File]::WriteAllBytes($lock, $locks[$lock]) }
        }
        $artifacts += [ordered]@{ component = $component; file = [IO.Path]::GetFileName($file); sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    }
    [ordered]@{ runtime = $Runtime; selfContained = $false; createdUtc = [DateTime]::UtcNow.ToString('o'); artifacts = $artifacts } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding UTF8
    Write-Host "Migration bundles generated in .local/migrations/$Runtime. No database was modified."
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
    Pop-Location
}
