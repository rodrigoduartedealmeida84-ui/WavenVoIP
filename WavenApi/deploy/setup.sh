#!/bin/bash
# setup.sh — Instalação/atualização da Waven API
# Execute como root a partir do diretório extraído do ZIP:
#   sudo bash setup.sh
#
# REGRA: appsettings.Production.json existente NUNCA é sobrescrito.
#        Backup automático antes de qualquer alteração.

set -euo pipefail

INSTALL_DIR="/opt/waven-api"
SERVICE="waven-api"
USER="wavenapi"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONFIG="$INSTALL_DIR/appsettings.Production.json"
CONFIG_BACKUP=""

echo ""
echo "=== WavenApi Setup/Update ==="
echo "    Origem:  $SCRIPT_DIR"
echo "    Destino: $INSTALL_DIR"
echo ""

# Root check
if [[ $EUID -ne 0 ]]; then
    echo "ERRO: execute como root:  sudo bash setup.sh"
    exit 1
fi

# Backup do config ANTES de qualquer alteração
if [[ -f "$CONFIG" ]]; then
    CONFIG_BACKUP="${CONFIG}.bak.$(date +%Y%m%d_%H%M%S)"
    cp "$CONFIG" "$CONFIG_BACKUP"
    echo "[OK] Backup do config: $CONFIG_BACKUP"
fi

# Criar usuário do serviço se não existir
if ! id -u "$USER" &>/dev/null; then
    useradd -r -s /sbin/nologin "$USER"
    echo "[OK] Usuário $USER criado"
else
    echo "[OK] Usuário $USER já existe"
fi

# Parar serviço se estiver rodando
if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
    systemctl stop "$SERVICE"
    echo "[OK] Serviço parado para atualização"
fi

# Criar diretórios
mkdir -p "$INSTALL_DIR/data" "$INSTALL_DIR/logs"

# Copiar binários — appsettings.Production.json EXCLUÍDO do rsync
rsync -a \
    --exclude="setup.sh" \
    --exclude="waven-api.service" \
    --exclude="appsettings.Production.json" \
    --exclude="appsettings.Production.json.example" \
    --exclude="appsettings.Development.json" \
    "$SCRIPT_DIR/" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/WavenApi"
echo "[OK] Binários copiados (config de produção preservado)"

# Verificar/criar config se for primeira instalação
if [[ ! -f "$CONFIG" ]]; then
    echo ""
    echo "=== PRIMEIRA INSTALAÇÃO — CONFIG NECESSÁRIO ==="
    if [[ -f "$SCRIPT_DIR/appsettings.Production.json.example" ]]; then
        cp "$SCRIPT_DIR/appsettings.Production.json.example" "$CONFIG"
        echo "  Config de exemplo copiado para: $CONFIG"
    else
        # Criar template mínimo
        cat > "$CONFIG" << 'CFGEOF'
{
  "WavenApi": {
    "BearerToken": "SUBSTITUA_POR_TOKEN_SEGURO_MINIMO_32_CARACTERES",
    "Ami": {
      "Host": "127.0.0.1",
      "Port": 5038,
      "User": "USUARIO_AMI",
      "Password": "SENHA_AMI"
    },
    "MySql": {
      "Host": "127.0.0.1",
      "Port": 3306,
      "Database": "asteriskcdrdb",
      "User": "USUARIO_MYSQL",
      "Password": "SENHA_MYSQL"
    }
  }
}
CFGEOF
        echo "  Template de config criado em: $CONFIG"
    fi
    chown -R "$USER:$USER" "$INSTALL_DIR"
    chmod 750 "$INSTALL_DIR"
    chmod 770 "$INSTALL_DIR/data" "$INSTALL_DIR/logs"
    chmod 640 "$CONFIG"
    cp "$SCRIPT_DIR/waven-api.service" /etc/systemd/system/
    systemctl daemon-reload
    systemctl enable "$SERVICE" 2>/dev/null || true
    echo ""
    echo "  EDITE o config antes de iniciar o serviço:"
    echo "    nano $CONFIG"
    echo ""
    echo "  Campos obrigatórios:"
    echo "    WavenApi.BearerToken        (mín. 32 chars)"
    echo "    WavenApi.Ami.User/Password  (usuário AMI do Asterisk)"
    echo "    WavenApi.MySql.User/Password"
    echo ""
    echo "  Após editar:"
    echo "    sudo systemctl start $SERVICE"
    echo "=== Setup parcial concluído — complete a configuração acima ==="
    exit 0
fi

# Validar seções obrigatórias
echo ""
echo "--- Validando configuração ---"
VALID=true

check_json() {
    python3 -c "$1" 2>/dev/null
}

# BearerToken não pode ser placeholder
if ! check_json "
import json,sys
cfg=json.load(open('$CONFIG'))
t=cfg.get('WavenApi',{}).get('BearerToken','')
sys.exit(0 if t and 'SUBSTITUA' not in t and len(t)>=16 else 1)"; then
    echo "  ERRO: WavenApi.BearerToken ausente ou ainda é o placeholder"
    VALID=false
fi

# Seção Ami deve existir
if ! check_json "
import json,sys
cfg=json.load(open('$CONFIG'))
sys.exit(0 if 'Ami' in cfg.get('WavenApi',{}) else 1)"; then
    echo "  AVISO: WavenApi.Ami não configurado — ramais ao vivo desativados"
fi

# Seção MySql deve existir
if ! check_json "
import json,sys
cfg=json.load(open('$CONFIG'))
sys.exit(0 if 'MySql' in cfg.get('WavenApi',{}) else 1)"; then
    echo "  AVISO: WavenApi.MySql não configurado — CDR desativado"
fi

if [[ "$VALID" == "false" ]]; then
    echo ""
    echo "=== DEPLOY ABORTADO — config inválido ==="
    if [[ -n "$CONFIG_BACKUP" ]]; then
        cp "$CONFIG_BACKUP" "$CONFIG"
        echo "  Config restaurado do backup: $CONFIG_BACKUP"
    fi
    echo "  Corrija $CONFIG e rode setup.sh novamente."
    exit 1
fi
echo "  Configuração OK"

# Ajustar permissões
chown -R "$USER:$USER" "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR"
chmod 770 "$INSTALL_DIR/data" "$INSTALL_DIR/logs"
chmod 640 "$CONFIG"
echo "[OK] Permissões definidas"

# Instalar serviço systemd
cp "$SCRIPT_DIR/waven-api.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable "$SERVICE"
echo "[OK] Serviço systemd instalado"

# Iniciar serviço
systemctl start "$SERVICE"
sleep 3

if systemctl is-active --quiet "$SERVICE"; then
    echo "[OK] Serviço iniciado com sucesso"
    echo ""
    echo "=== Teste rápido ==="
    curl -sf http://127.0.0.1:5005/health && echo "" || echo "  /health: sem resposta"
    echo ""
    echo "=== Concluído ==="
    echo "  Status:  systemctl status $SERVICE"
    echo "  Logs:    journalctl -u $SERVICE -f"
    echo "  Config:  $CONFIG"
    [[ -n "$CONFIG_BACKUP" ]] && echo "  Backup:  $CONFIG_BACKUP"
else
    echo ""
    echo "ERRO: Serviço não iniciou. Logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager
    if [[ -n "$CONFIG_BACKUP" ]]; then
        cp "$CONFIG_BACKUP" "$CONFIG"
        echo "  Config restaurado do backup: $CONFIG_BACKUP"
    fi
    exit 1
fi
