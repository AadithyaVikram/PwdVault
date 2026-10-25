# Local/CI validation only; never publishes a GitHub release or installs an app.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = 'C:/Program Files/dotnet/dotnet.exe' }
    & $dotnet restore PwdVault.slnx --configfile NuGet.Config --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & $dotnet build PwdVault.slnx --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet test PwdVault.slnx --no-build --no-restore --configuration Release --logger 'trx;LogFileName=hardening.trx' --results-directory artifacts/hardening
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & (Join-Path $PSScriptRoot 'Test-Deployment.ps1')
    [IO.Directory]::CreateDirectory((Join-Path $workspace 'artifacts/hardening')) | Out-Null
    $audit = & $dotnet list PwdVault.slnx package --vulnerable --include-transitive --format json --output-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability query failed.' }
    $audit | Set-Content -LiteralPath artifacts/hardening/dependency-audit.json
    $result = $audit | ConvertFrom-Json
    $vulnerable = @($result.projects | ForEach-Object { $_.frameworks } | ForEach-Object { $_.topLevelPackages; $_.transitivePackages } | Where-Object { $_.vulnerabilities })
    if ($vulnerable.Count) { throw 'Known vulnerable packages were reported. Review the dependency audit.' }
    & $dotnet publish src/PwdVault.Desktop/PwdVault.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/win-x64 -p:RestoreConfigFile=NuGet.Config -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Get-ChildItem -LiteralPath artifacts/win-x64 -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring((Join-Path $workspace 'artifacts/win-x64').Length + 1)
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
    } | Set-Content -LiteralPath artifacts/hardening/SHA256SUMS.txt
    Write-Output 'Automated release checks passed. Signing, independent review, and interactive acceptance are separate gates.'
} finally { Pop-Location }
