#!/bin/bash
# deploy-server.sh — roda no servidor como root
# Uso: sudo bash /tmp/deploy-server.sh
set -euo pipefail

ZIP="/tmp/waven-api-update.zip"
DEST="/tmp/waven-deploy"
INSTALL="/opt/waven-api"
SVC="waven-api"
CONFIG="$INSTALL/appsettings.Production.json"
CONFIG_BACKUP=""

echo ""
echo "=== Deploy WavenAPI =========================="

# 1. Backup do config ANTES de tudo
if [[ -f "$CONFIG" ]]; then
    CONFIG_BACKUP="${CONFIG}.bak.$(date +%Y%m%d_%H%M%S)"
    cp "$CONFIG" "$CONFIG_BACKUP"
    echo "[OK] Backup do config: $CONFIG_BACKUP"
else
    echo "[AV] Config não encontrado em $CONFIG"
    if [[ ! -d "$INSTALL" ]]; then
        echo "     Parece ser primeira instalação."
    fi
fi

# 2. Backup do binário anterior
if [[ -d "$INSTALL" ]]; then
    BIN_BACKUP="${INSTALL}_backup_$(date +%Y%m%d_%H%M%S)"
    cp -a "$INSTALL" "$BIN_BACKUP"
    echo "[OK] Backup dos binários: $BIN_BACKUP"
fi

# 3. Extrair nova versão
rm -rf "$DEST"
mkdir -p "$DEST"
# unzip devolve exit code 1 quando so' encontra AVISOS (ex.: zips gerados no Windows
# via Compress-Archive/System.IO.Compression usam '\' como separador de path, o que
# gera "appears to use backslashes as path separators" mas extrai tudo certinho) —
# so' trata como erro fatal se o codigo for >=2 (erro real de arquivo corrompido/etc).
set +e
unzip -q "$ZIP" -d "$DEST"
UNZIP_CODE=$?
set -e
if [[ $UNZIP_CODE -ge 2 ]]; then
    echo "ERRO: unzip falhou com codigo $UNZIP_CODE" >&2
    exit $UNZIP_CODE
elif [[ $UNZIP_CODE -eq 1 ]]; then
    echo "[AV] unzip retornou avisos (codigo 1) — extracao concluida, seguindo"
fi
echo "[OK] ZIP extraído em $DEST"

# 4. Garantir que o config NÃO está no diretório extraído
#    (setup.sh não vai mais copiar config, mas removemos por segurança)
rm -f "$DEST/appsettings.Production.json"
echo "[OK] appsettings.Production.json removido do pacote extraído"

# 5. Verificar que o config real existe antes do setup
if [[ ! -f "$CONFIG" ]] && [[ -z "$CONFIG_BACKUP" ]]; then
    echo ""
    echo "=== PRIMEIRA INSTALAÇÃO ==="
    echo "  Não há config existente. O setup.sh criará um template."
    echo "  Você deverá preenchê-lo manualmente depois."
fi

# 6. Executar setup.sh (ele nunca sobrescreve o config)
chmod +x "$DEST/setup.sh"
bash "$DEST/setup.sh"
echo ""

# 7. Validar que o config sobreviveu
if [[ -f "$CONFIG" ]] && [[ -n "$CONFIG_BACKUP" ]]; then
    if diff -q "$CONFIG" "$CONFIG_BACKUP" > /dev/null 2>&1; then
        echo "[OK] Config idêntico ao backup — não foi alterado"
    else
        echo "[!] Config diferente do backup — verificar:"
        diff "$CONFIG_BACKUP" "$CONFIG" || true
    fi
fi

# 8. Testar endpoints localmente
echo ""
echo "--- Validação de endpoints ---"
sleep 3

test_endpoint() {
    local path="$1"
    local desc="$2"
    local code
    code=$(curl -o /dev/null -s -w "%{http_code}" --max-time 10 "http://127.0.0.1:5005${path}" 2>/dev/null || echo "ERR")
    echo "  ${path} : HTTP $code — $desc"
    echo "$code"
}

H=$(test_endpoint "/health" "health check" | tail -1)
S=$(test_endpoint "/api/ami/status" "status AMI" | tail -1)
P=$(test_endpoint "/api/ami/peers" "peers (401=ok sem token)" | tail -1)
L=$(test_endpoint "/api/ami/live-extensions" "live-ext (401=ok sem token)" | tail -1)
Q=$(test_endpoint "/api/ami/queues-live" "queues-live (401=ok sem token)" | tail -1)

echo ""
echo "--- Resumo ---"
[[ "$H" == "200" ]] && echo "  ✔ /health" || echo "  ✘ /health: HTTP $H"

if [[ "$P" == "200" || "$P" == "401" || "$P" == "403" ]]; then
    echo "  ✔ /api/ami/peers existe (HTTP $P)"
else
    echo "  ✘ /api/ami/peers: HTTP $P — endpoint não encontrado"
fi

if [[ "$L" == "200" || "$L" == "401" || "$L" == "403" ]]; then
    echo "  ✔ /api/ami/live-extensions existe (HTTP $L)"
else
    echo "  ✘ /api/ami/live-extensions: HTTP $L — endpoint não encontrado"
fi

if [[ "$Q" == "200" || "$Q" == "401" || "$Q" == "403" ]]; then
    echo "  ✔ /api/ami/queues-live existe (HTTP $Q)"
else
    echo "  ✘ /api/ami/queues-live: HTTP $Q — endpoint não encontrado (nova versão necessária)"
fi

echo ""
echo "=== Deploy concluído ========================="
[[ -n "$CONFIG_BACKUP" ]] && echo "  Backup config: $CONFIG_BACKUP"
[[ -n "${BIN_BACKUP:-}" ]]  && echo "  Backup bins:   $BIN_BACKUP"
echo "  Logs: journalctl -u $SVC -f"
echo "  Config: $CONFIG"
