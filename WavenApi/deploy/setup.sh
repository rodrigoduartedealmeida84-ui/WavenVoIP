#!/bin/bash
# Instalacao/atualizacao do WavenApi no servidor
# Execute a partir do diretorio onde o zip foi extraido:
#   sudo bash setup.sh

set -euo pipefail

INSTALL_DIR="/opt/waven-api"
SERVICE="waven-api"
USER="wavenapi"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo ""
echo "=== WavenApi Setup ==="
echo "    Origem:  $SCRIPT_DIR"
echo "    Destino: $INSTALL_DIR"
echo ""

# Verificar root
if [[ $EUID -ne 0 ]]; then
    echo "ERRO: execute como root:  sudo bash setup.sh"
    exit 1
fi

# Verificar appsettings.Production.json antes de instalar
if [[ ! -f "$SCRIPT_DIR/appsettings.Production.json" ]]; then
    echo "ERRO: appsettings.Production.json nao encontrado em $SCRIPT_DIR"
    echo ""
    echo "Crie o arquivo com o BearerToken antes de continuar:"
    echo ""
    echo '  cat > '"$SCRIPT_DIR"'/appsettings.Production.json << '"'"'EOF'"'"
    echo '  {'
    echo '    "WavenApi": {'
    echo '      "BearerToken": "SEU_TOKEN_AQUI_MINIMO_32_CHARS"'
    echo '    }'
    echo '  }'
    echo '  EOF'
    echo ""
    exit 1
fi

# Criar usuario do servico se nao existir
if ! id -u "$USER" &>/dev/null; then
    useradd -r -s /sbin/nologin "$USER"
    echo "[OK] Usuario $USER criado"
else
    echo "[OK] Usuario $USER ja existe"
fi

# Parar servico se estiver rodando
if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
    systemctl stop "$SERVICE"
    echo "[OK] Servico parado para atualizacao"
fi

# Criar diretorios
mkdir -p "$INSTALL_DIR/data" "$INSTALL_DIR/logs"
echo "[OK] Diretorios criados"

# Copiar binarios
rsync -a --exclude="setup.sh" --exclude="waven-api.service" \
         --exclude="appsettings.Production.json.example" \
         "$SCRIPT_DIR/" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/WavenApi"
echo "[OK] Binarios copiados para $INSTALL_DIR"

# Ajustar permissoes
chown -R "$USER:$USER" "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR"
chmod 770 "$INSTALL_DIR/data" "$INSTALL_DIR/logs"
# appsettings.Production.json: so o usuario do servico pode ler
chmod 640 "$INSTALL_DIR/appsettings.Production.json"
echo "[OK] Permissoes definidas"

# Instalar servico systemd
cp "$SCRIPT_DIR/waven-api.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable "$SERVICE"
echo "[OK] Servico systemd instalado e habilitado"

# Iniciar servico
systemctl start "$SERVICE"
sleep 3

if systemctl is-active --quiet "$SERVICE"; then
    echo "[OK] Servico iniciado com sucesso"
    echo ""
    echo "=== Teste rapido ==="
    curl -sf http://127.0.0.1:5005/health && echo ""
    echo ""
    echo "=== Concluido ==="
    echo "  Status:  systemctl status $SERVICE"
    echo "  Logs:    journalctl -u $SERVICE -f"
    echo "  Config:  $INSTALL_DIR/appsettings.Production.json"
else
    echo ""
    echo "ERRO: Servico nao iniciou. Logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager
    exit 1
fi
