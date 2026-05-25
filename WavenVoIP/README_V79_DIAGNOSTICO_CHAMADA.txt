WavenVoIP v79 - Diagnóstico de chamadas por saída

Atualização aplicada:
- A tela de chamada agora mostra a saída usada: Operadora, WhatsApp TIM ou WhatsApp Vivo.
- Quando a chamada falha, a tela permanece aberta e mostra o destino SIP enviado ao Issabel.
- O erro e o número enviado são copiados para a área de transferência para facilitar o envio no suporte.
- Foi reforçado o log SIP em: %LOCALAPPDATA%\WavenVoIP\sip_signal_debug.log

Objetivo:
Descobrir se o problema está no aplicativo, na rota do Issabel, no tronco WhatsApp ou na permissão do ramal.

Teste recomendado:
1) Ligar pela Operadora.
2) Ligar pelo WhatsApp TIM.
3) Ligar pelo WhatsApp Vivo.
4) Conferir na tela e no log o número final enviado, exemplo 1+número, 2+número ou 3+número.
