# WavenApi -- Validacao local completa
# Uso: cd [raiz-da-solucao]; .\WavenApi\test-local.ps1
# Flags: -KeepDb  (nao apaga o banco de teste antes de rodar)
param([switch]$KeepDb)

$BASE  = "http://127.0.0.1:5005"
$TOKEN = "TOKEN_LOCAL_TESTE_12345678901234567890"
$PASS  = 0; $FAIL = 0
$FALHAS = @()
$proc  = $null

function ok($name) {
    $script:PASS++
    Write-Host "  [PASS] $name" -ForegroundColor Green
}
function fail($name, $msg) {
    $script:FAIL++
    $script:FALHAS += "  FAIL: $name -- $msg"
    Write-Host "  [FAIL] $name" -ForegroundColor Red
    Write-Host "         $msg"  -ForegroundColor DarkRed
}

function api {
    param(
        [string]$method,
        [string]$path,
        $body      = $null,
        [switch]$noAuth,
        [switch]$badToken
    )
    $p = @{
        Uri         = "$BASE$path"
        Method      = $method
        ContentType = "application/json"
        ErrorAction = "Stop"
    }
    if (-not $noAuth) {
        $tok = if ($badToken) { "TOKEN_ERRADO_XPTO" } else { $TOKEN }
        $p["Headers"] = @{ Authorization = "Bearer $tok" }
    }
    if ($body) { $p["Body"] = ($body | ConvertTo-Json -Compress) }
    try {
        $r = Invoke-RestMethod @p
        return @{ ok=$true; status=200; r=$r }
    } catch {
        $code = 0
        if ($_.Exception.Response) {
            try { $code = [int]$_.Exception.Response.StatusCode } catch {}
        }
        if ($code -eq 0 -and $_.Exception.Message -match '\((\d{3})\)') { $code = [int]$Matches[1] }
        return @{ ok=$false; status=$code; err=$_.Exception.Message }
    }
}

# dotnet run seta working dir como o diretorio do projeto (WavenApi/)
# O banco de teste fica em WavenApi\local-test\waven-test.db
$dbDir = "WavenApi\local-test"
if (-not $KeepDb) {
    @("$dbDir\waven-test.db","$dbDir\waven-test.db-wal","$dbDir\waven-test.db-shm") |
        Where-Object { Test-Path $_ } |
        ForEach-Object { Remove-Item $_ -Force }
}

# Verificar se ja existe instancia rodando
Write-Host ""
$existente = api GET "/health" -noAuth
if ($existente.ok) {
    Write-Host "  INFO: API ja esta rodando em 5005. Usando instancia existente." -ForegroundColor Yellow
} else {
    Write-Host "  Iniciando WavenApi (ASPNETCORE_ENVIRONMENT=Development)..." -ForegroundColor Cyan
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $proc = Start-Process dotnet `
        -ArgumentList "run --project WavenApi/WavenApi.csproj --no-build" `
        -PassThru -NoNewWindow `
        -RedirectStandardOutput "waven-api-test-stdout.log" `
        -RedirectStandardError  "waven-api-test-stderr.log"

    $ready = $false
    for ($i = 1; $i -le 40; $i++) {
        Start-Sleep -Seconds 1
        $h = api GET "/health" -noAuth
        if ($h.ok) { $ready = $true; break }
        if ($i % 5 -eq 0) { Write-Host "  aguardando API... ($i/40)" }
    }
    if (-not $ready) {
        Write-Host ""
        Write-Host "  ERRO: API nao respondeu em 40s." -ForegroundColor Red
        if (Test-Path "waven-api-test-stderr.log") {
            Write-Host "  --- stderr ---"
            Get-Content "waven-api-test-stderr.log" | Select-Object -First 30 |
                ForEach-Object { Write-Host "  $_" }
        }
        if ($null -ne $proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        exit 1
    }
}

Write-Host "  API pronta." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Executando 24 testes..." -ForegroundColor Cyan
Write-Host ""

# T01 -- GET /health sem auth
$r = api GET "/health" -noAuth
if ($r.ok -and $r.r.status -eq "ok") { ok "T01 GET /health (sem auth) -> 200 {status:ok}" }
else { fail "T01 GET /health" "status=$($r.r.status) ok=$($r.ok) code=$($r.status)" }

# T02 -- GET /api/contacts sem Authorization -> 401
$r = api GET "/api/contacts?ramal=100" -noAuth
if (-not $r.ok -and $r.status -eq 401) { ok "T02 GET /api/contacts sem auth -> 401" }
else { fail "T02 Sem auth" "Esperava 401, ok=$($r.ok) status=$($r.status)" }

# T03 -- GET /api/contacts token errado -> 401
$r = api GET "/api/contacts?ramal=100" -badToken
if (-not $r.ok -and $r.status -eq 401) { ok "T03 GET /api/contacts token errado -> 401" }
else { fail "T03 Token errado" "Esperava 401, ok=$($r.ok) status=$($r.status)" }

# T04 -- GET /api/contacts lista inicial
$r = api GET "/api/contacts?ramal=100"
if ($r.ok) { ok "T04 GET /api/contacts (lista inicial ok, $(@($r.r).Count) contatos)" }
else { fail "T04 GET /api/contacts" "$($r.err)" }

# T05 -- POST /api/contacts criar contato
$cid = $null
$r = api POST "/api/contacts" @{
    nome      = "Joao Teste"
    numero    = "(66) 99999-1234"
    empresa   = "Empresa Teste"
    observacao = "Obs de teste"
    criadoPor = "100"
}
if ($r.ok -and $r.r.id) {
    $cid = $r.r.id
    ok "T05 POST /api/contacts -- criado (id=$cid)"
} else { fail "T05 POST criar contato" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T06 -- POST duplicado -> 409
$r = api POST "/api/contacts" @{ nome="Joao Dup"; numero="(66) 99999-1234"; criadoPor="100" }
if (-not $r.ok -and $r.status -eq 409) { ok "T06 POST duplicado -> 409 Conflict" }
else { fail "T06 Duplicado" "Esperava 409, ok=$($r.ok) status=$($r.status)" }

# T07 -- GET /api/contacts contato aparece
$r = api GET "/api/contacts?ramal=100"
$c = @($r.r) | Where-Object { $_.id -eq $cid }
if ($r.ok -and ($c | Measure-Object).Count -gt 0) { ok "T07 GET /api/contacts -- contato aparece" }
else { fail "T07 Contato nao aparece" "id=$cid" }

# T08 -- PUT /api/contacts/{id} editar
$r = api PUT "/api/contacts/$cid" @{
    nome       = "Joao Editado"
    empresa    = "Empresa Nova"
    observacao = "Obs editada"
    atualizadoPor = "100"
}
if ($r.ok -and $r.r.nome -eq "Joao Editado") { ok "T08 PUT /api/contacts/{id} -- nome atualizado" }
else { fail "T08 PUT editar" "ok=$($r.ok) nome=$($r.r.nome) status=$($r.status)" }

# T09 -- POST favorito tipo usuario (ramal 100)
$r = api POST "/api/contacts/$cid/favorite" @{ ramal="100"; tipoFavorito="usuario"; criadoPor="100" }
if ($r.ok) { ok "T09 POST .../favorite tipo=usuario ramal=100" }
else { fail "T09 Favorito usuario" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T10 -- GET ramal 100: isFavorito=true
$r = api GET "/api/contacts?ramal=100"
$c = @($r.r) | Where-Object { $_.id -eq $cid }
if ($r.ok -and $c -and $c.isFavorito -eq $true) { ok "T10 GET ramal=100 -> isFavorito=true" }
else { fail "T10 isFavorito ramal 100" "isFavorito=$($c.isFavorito)" }

# T11 -- GET ramal 200: isFavorito=false (isolamento por ramal)
$r = api GET "/api/contacts?ramal=200"
$c = @($r.r) | Where-Object { $_.id -eq $cid }
if ($r.ok -and $c -and -not $c.isFavorito) { ok "T11 GET ramal=200 -> isFavorito=false (isolamento)" }
else { fail "T11 Isolamento ramal" "isFavorito=$($c.isFavorito)" }

# T12 -- DELETE favorito usuario
$r = api DELETE "/api/contacts/$cid/favorite" @{ ramal="100"; tipoFavorito="usuario" }
if ($r.ok) { ok "T12 DELETE .../favorite usuario" }
else { fail "T12 Remove favorito" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T13 -- POST favorito tipo global (simula migracao dos 7 favoritos atuais)
$r = api POST "/api/contacts/$cid/favorite" @{ tipoFavorito="global"; criadoPor="system" }
if ($r.ok) { ok "T13 POST .../favorite tipo=global (simula migracao system)" }
else { fail "T13 Favorito global" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T14 -- GET ramal 200 ve favorito global
$r = api GET "/api/contacts?ramal=200"
$c = @($r.r) | Where-Object { $_.id -eq $cid }
if ($r.ok -and $c -and $c.isFavorito -eq $true -and $c.tipoFavorito -eq "global") {
    ok "T14 GET ramal=200 -> isFavorito=true tipoFavorito=global"
} else { fail "T14 Global visivel em ramal 200" "isFavorito=$($c.isFavorito) tipo=$($c.tipoFavorito)" }

# T15 -- GET /api/contacts/sync favoritosAtuais inclui favorito global
$since = [System.Uri]::EscapeDataString("2000-01-01T00:00:00Z")
$r = api GET "/api/contacts/sync?since=$since&ramal=100"
$cidInFav = @($r.r.favoritosAtuais) -contains $cid
if ($r.ok -and $r.r.contacts -and $r.r.favoritosAtuais -and $cidInFav) {
    ok "T15 GET /sync -> $(@($r.r.contacts).Count) contatos, $(@($r.r.favoritosAtuais).Count) fav(s), cid em favoritosAtuais"
} else { fail "T15 /sync favoritosAtuais" "ok=$($r.ok) cidInFav=$cidInFav" }

# T16 -- GET /sync incremental: since=futuro -> contacts vazio
$future = [System.Uri]::EscapeDataString([DateTime]::UtcNow.AddMinutes(5).ToString("O"))
$r = api GET "/api/contacts/sync?since=$future&ramal=100"
$count = @($r.r.contacts).Count
if ($r.ok -and $count -eq 0) { ok "T16 /sync since=futuro -> contacts=[] (incremental ok)" }
else { fail "T16 /sync incremental" "count=$count esperava 0" }

# T17 -- DELETE /api/contacts/{id} soft delete
$r = api DELETE "/api/contacts/$cid" @{ excluidoPor="100" }
if ($r.ok) { ok "T17 DELETE /api/contacts/{id} (soft delete)" }
else { fail "T17 Soft delete" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T18 -- GET /api/contacts: excluido nao aparece
$r = api GET "/api/contacts?ramal=100"
$c = @($r.r) | Where-Object { $_.id -eq $cid }
if ($r.ok -and ($c | Measure-Object).Count -eq 0) { ok "T18 GET /api/contacts -- excluido nao aparece" }
else { fail "T18 Excluido oculto" "Contato ainda aparece na lista" }

# T19 -- GET /sync: tombstone aparece com excluido=true
$since2 = [System.Uri]::EscapeDataString("2000-01-01T00:00:00Z")
$r = api GET "/api/contacts/sync?since=$since2&ramal=100"
$tc = @($r.r.contacts) | Where-Object { $_.id -eq $cid }
if ($r.ok -and ($tc | Measure-Object).Count -gt 0 -and $tc.excluido) {
    ok "T19 /sync -- tombstone aparece (excluido=true)"
} else { fail "T19 Tombstone no sync" "ok=$($r.ok) tcCount=$(($tc|Measure-Object).Count) excluido=$($tc.excluido)" }

# T20 -- POST mesmo numero do excluido -> reativa (mesmo id)
$r = api POST "/api/contacts" @{ nome="Joao Reativado"; numero="(66) 99999-1234"; criadoPor="200" }
if ($r.ok -and $r.r.id -eq $cid) { ok "T20 POST mesmo numero -> reativa contato excluido (mesmo id=$cid)" }
else { fail "T20 Reativacao" "ok=$($r.ok) id=$($r.r.id) esperava=$cid" }

# T21 -- GET /api/company/config
$r = api GET "/api/company/config"
if ($r.ok -and $null -ne $r.r.configJson) { ok "T21 GET /api/company/config -> {configJson, atualizadoEm}" }
else { fail "T21 GET /company/config" "ok=$($r.ok)" }

# T22 -- PUT /api/company/config
$cfgJson = @{ whatsappApiUrl = "https://api.teste.com/v1"; whatsappToken = "tok-teste-123" } |
    ConvertTo-Json -Compress
$r = api PUT "/api/company/config" @{ configJson = $cfgJson; atualizadoPor = "100" }
if ($r.ok) { ok "T22 PUT /api/company/config -> salvo" }
else { fail "T22 PUT /company/config" "ok=$($r.ok) status=$($r.status) err=$($r.err)" }

# T23 -- GET /api/company/config verifica persistencia
$r = api GET "/api/company/config"
$saved = $null
if ($r.r.configJson) { $saved = $r.r.configJson | ConvertFrom-Json -ErrorAction SilentlyContinue }
if ($r.ok -and $saved -and $saved.whatsappApiUrl -eq "https://api.teste.com/v1") {
    ok "T23 GET /company/config -> configJson persistido corretamente"
} else { fail "T23 Persistencia config" "configJson=$($r.r.configJson)" }

# T24 -- POST /api/contacts campos invalidos -> 400
$r = api POST "/api/contacts" @{ nome=""; numero=""; criadoPor="100" }
if (-not $r.ok -and $r.status -eq 400) { ok "T24 POST campos invalidos -> 400" }
else { fail "T24 Validacao 400" "Esperava 400, ok=$($r.ok) status=$($r.status)" }

# Resultado
Write-Host ""
Write-Host ("  " + ("=" * 58))
$cor = if ($FAIL -eq 0) { "Green" } else { "Red" }
Write-Host "  RESULTADO: $PASS PASSOU  |  $FAIL FALHOU  (de $($PASS+$FAIL) testes)" -ForegroundColor $cor
Write-Host ("  " + ("=" * 58))

if ($FAIL -gt 0) {
    Write-Host ""
    Write-Host "  Falhas:" -ForegroundColor Red
    $FALHAS | ForEach-Object { Write-Host $_ -ForegroundColor Red }
}

# Cleanup
if ($null -ne $proc) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
Remove-Item "waven-api-test-stdout.log","waven-api-test-stderr.log" -ErrorAction SilentlyContinue

Write-Host ""
if ($FAIL -gt 0) { exit 1 } else { exit 0 }
