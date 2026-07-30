# Test-VersionJsonEncoding.ps1 -- valida que o round-trip de leitura/escrita do
# version.json (mesma logica usada em Publish-Clean.ps1) preserva acentos,
# cedilha e travessoes. Nao toca no version.json real -- roda inteiramente em
# um arquivo temporario.
#
# Observacao: os caracteres especiais de teste sao montados via [char] (codigo
# unicode) em vez de literais no arquivo, para que o teste nao dependa do
# encoding com que este proprio .ps1 foi salvo em disco.
# Uso: powershell -ExecutionPolicy Bypass -File Test-VersionJsonEncoding.ps1

$ErrorActionPreference = "Stop"

$tmpFile = Join-Path $env:TEMP "wavenvoip-version-encoding-test.json"

$aTil    = [char]0x00E3   # a
$cCed    = [char]0x00E7   # c
$eAcc    = [char]0x00E9   # e
$enDash  = [char]0x2013   # -
$emDash  = [char]0x2014   # --

$notasEsperadas = "Ajustes de configuracao $enDash funcao, a${cCed}${aTil}o, aten${cCed}${aTil}o $emDash tudo revisado. Caracteres: $aTil $cCed $eAcc $enDash $emDash"

$original = [PSCustomObject]@{
    versao = "9.9.9"
    zip    = "https://example.com/x.zip"
    sha256 = "deadbeef"
    data   = "2026-01-01"
    notas  = $notasEsperadas
}

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($tmpFile, ($original | ConvertTo-Json -Depth 5), $utf8NoBom)

# Mesma logica de leitura/escrita do Publish-Clean.ps1 passo 6
$roundTripped = Get-Content $tmpFile -Raw -Encoding UTF8 | ConvertFrom-Json
$roundTripped.versao = "9.9.10"
$roundTrippedText = $roundTripped | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($tmpFile, $roundTrippedText, $utf8NoBom)

$final = Get-Content $tmpFile -Raw -Encoding UTF8 | ConvertFrom-Json

$ok = $true
if ($final.notas -ne $notasEsperadas) {
    Write-Host "FALHA: notas corrompidas apos round-trip." -ForegroundColor Red
    Write-Host "  esperado: $notasEsperadas"
    Write-Host "  obtido  : $($final.notas)"
    $ok = $false
}
if ($final.versao -ne "9.9.10") {
    Write-Host "FALHA: versao nao foi atualizada corretamente." -ForegroundColor Red
    $ok = $false
}

# Confirma que o arquivo final NAO tem BOM (primeiro byte nao deve ser 0xEF)
$bytes = [System.IO.File]::ReadAllBytes($tmpFile)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    Write-Host "FALHA: arquivo foi salvo com BOM UTF-8." -ForegroundColor Red
    $ok = $false
}

Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue

if ($ok) {
    Write-Host "OK -- encoding preservado (acentos, cedilha, travessoes) e sem BOM." -ForegroundColor Green
    exit 0
} else {
    exit 1
}
