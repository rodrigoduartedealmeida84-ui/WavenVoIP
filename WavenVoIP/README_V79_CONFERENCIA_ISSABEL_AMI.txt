Correção de conferência via Issabel/AMI

Esta versão muda a conferência para não tentar misturar áudio localmente no app.
O botão Conferência agora usa o Issabel/Asterisk:

1. Pergunta o participante.
2. Pergunta a sala de conferência do Issabel/ConfBridge.
3. Via AMI, localiza os canais da chamada ativa do ramal.
4. Redireciona os canais da chamada para a sala.
5. Origina o novo participante para a mesma sala.

Requisitos no Issabel:
- AMI habilitado com permissão para CoreShowChannels, Redirect e Originate.
- A sala de conferência informada deve existir no Issabel/FreePBX.
- Exemplo: criar uma sala/ramal de conferência como 800 ou 8000.

Logs:
- %LOCALAPPDATA%\WavenVoIP\sip_signal_debug.log
Procure por linhas começando com CONF AMI.
