# Liga ou desliga o nivelamento automatico do microfone (WavenVoIP 2.5.0) para comparar
# chamadas com e sem ele. Feche o WavenVoIP antes: o app regrava o sipconfig.json ao sair.
#   .\Definir-MicAutoGain.ps1 -Estado Desligado
#   .\Definir-MicAutoGain.ps1 -Estado Ligado
param([Parameter(Mandatory)][ValidateSet('Ligado','Desligado')][string]$Estado)

if (Get-Process WavenVoIP -ErrorAction SilentlyContinue) {
    Write-Host 'Feche o WavenVoIP antes de mudar a opcao.' -ForegroundColor Yellow; exit 1
}
$arq = Join-Path $env:LOCALAPPDATA 'WavenVoIP\sipconfig.json'
$txt = [IO.File]::ReadAllText($arq)
$valor = if ($Estado -eq 'Ligado') { 'true' } else { 'false' }
if ($txt -match '"MicAutoGainEnabled"\s*:\s*(true|false)') {
    $txt = [regex]::Replace($txt, '("MicAutoGainEnabled"\s*:\s*)(true|false)', "`${1}$valor")
} else {
    # Campo ainda nao gravado (primeira execucao da 2.5.0): insere logo apos a chave de abertura.
    $txt = [regex]::Replace($txt, '^\s*\{', "{`r`n  `"MicAutoGainEnabled`": $valor,", 1)
}
Copy-Item $arq "$arq.bak-micgain" -Force
[IO.File]::WriteAllText($arq, $txt, (New-Object Text.UTF8Encoding($false)))
Write-Host "MicAutoGainEnabled = $valor (copia anterior em sipconfig.json.bak-micgain)"
