# Publish-Clean.ps1 -- Build limpo e verificado do WavenVoIP
# Garante que NENHUM dado de usuario entre no publish/ZIP/instalador.
# Uso: powershell -ExecutionPolicy Bypass -File Publish-Clean.ps1

Set-Location $PSScriptRoot
$ErrorActionPreference = "Stop"

$sensitivePatterns = @(
    "sipconfig.json", "sipconfig.backup.json",
    "contatos.json", "historico.json",
    "audio-config.json", "whatsapp_config.json", "whatsapp_envios.json",
    "google_contacts_cache.json", "user.config", "settings.json",
    "crash.log", "*.db", "*.log"
)

Write-Host "=== WavenVoIP -- Publish Limpo ===" -ForegroundColor Cyan

# 1. Limpar artefatos anteriores
Write-Host ""
Write-Host "[1/6] Limpando bin, obj, publish..."
@("WavenVoIP\bin", "WavenVoIP\obj", "WavenUpdater\bin", "WavenUpdater\obj", "publish") |
    Where-Object { Test-Path $_ } |
    ForEach-Object { Remove-Item $_ -Recurse -Force; Write-Host "  Removido: $_" }

# 2. Build WavenUpdater
Write-Host ""
Write-Host "[2/6] Compilando WavenUpdater Release..."
dotnet build WavenUpdater\WavenUpdater.csproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "ERRO: WavenUpdater build falhou." }

# 3. Publish WavenVoIP
Write-Host ""
Write-Host "[3/6] Publicando WavenVoIP Release..."
dotnet publish WavenVoIP\WavenVoIP.csproj -c Release -r win-x64 --self-contained false -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "ERRO: WavenVoIP publish falhou." }

# 4. Verificar publish -- nenhum arquivo sensivel pode estar la
Write-Host ""
Write-Host "[4/6] Verificando publish por dados sensiveis..."
$found = @()
foreach ($pattern in $sensitivePatterns) {
    $matches2 = Get-ChildItem publish -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue
    if ($matches2) { $found += $matches2 }
}

if ($found.Count -gt 0) {
    Write-Host "  CRITICO: Arquivos sensiveis encontrados no publish:" -ForegroundColor Red
    $found | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Red }
    Write-Error "ABORTADO: Dados de usuario detectados no publish. Nunca distribuir este build."
}
Write-Host "  OK -- Nenhum dado de usuario no publish." -ForegroundColor Green

# 5. Criar ZIP e calcular SHA-256
Write-Host ""
Write-Host "[5/6] Criando WavenVoIP.zip..."
# PS 5.1 so supports .zip extension in Compress-Archive — create as temp zip then rename
if (Test-Path "WavenVoIP.zip")   { Remove-Item "WavenVoIP.zip"   -Force }
if (Test-Path "_tmp_waven.zip")  { Remove-Item "_tmp_waven.zip"  -Force }
Compress-Archive -Path "publish\*" -DestinationPath "_tmp_waven.zip"
Rename-Item "_tmp_waven.zip" "WavenVoIP.zip"
$sha256 = (Get-FileHash "WavenVoIP.zip" -Algorithm SHA256).Hash.ToLower()
$zipMB  = [math]::Round((Get-Item "WavenVoIP.zip").Length / 1MB, 2)
Write-Host "  PKG: $zipMB MB"
Write-Host "  SHA-256: $sha256"

# 6. Atualizar version.json
Write-Host ""
Write-Host "[6/6] Atualizando servidor_hostinger\version.json..."

# Auto-detect version from VersionService.cs
$versionLine = Select-String -Path "WavenVoIP\Services\VersionService.cs" -Pattern 'Versao\s*=\s*"([^"]+)"'
if ($versionLine) {
    $currentVersion = $versionLine.Matches[0].Groups[1].Value
    Write-Host "  Versao detectada: $currentVersion"
} else {
    Write-Error "Nao foi possivel detectar versao do VersionService.cs"
}

$versionFile = "servidor_hostinger\version.json"
$vJson = Get-Content $versionFile -Raw | ConvertFrom-Json
$githubZipUrl = "https://github.com/rodrigoduartedealmeida84-ui/WavenVoIP/releases/download/v$currentVersion/WavenVoIP.zip"
$vJson.versao = $currentVersion
$vJson.zip    = $githubZipUrl
$vJson.sha256 = $sha256
$vJson.data   = (Get-Date -Format "yyyy-MM-dd")
$vJson | ConvertTo-Json -Depth 5 | Out-File $versionFile -Encoding utf8

Write-Host ""
Write-Host "=== Publish concluido com sucesso ===" -ForegroundColor Green
Write-Host "  WavenVoIP.zip   : $zipMB MB"
Write-Host "  Versao          : $currentVersion"
Write-Host "  SHA-256         : $sha256"
Write-Host "  version.json    : atualizado"
Write-Host "  ZIP URL         : $githubZipUrl"
Write-Host ""
Write-Host "Proximo passo:" -ForegroundColor Yellow
Write-Host "  1. Compilar instalador:"
Write-Host "     $env:LOCALAPPDATA\Programs\`"Inno Setup 6`"\ISCC.exe WavenVoIP_Setup.iss"
Write-Host "  2. Commitar e push:"
Write-Host "     git add servidor_hostinger\version.json"
Write-Host "     git commit -m `"release v$currentVersion`""
Write-Host "     git push origin main"
Write-Host "  3. Criar GitHub Release v$currentVersion e anexar:"
Write-Host "     - WavenVoIP.zip"
Write-Host "     - WavenVoIP_Setup.exe"
Write-Host "  4. SHA local deve ser identico ao SHA do download do GitHub Release."
