Correção de conferência

- Sala padrão do sistema mantida em 800 quando não configurada.
- Campo de sala removido da tela para evitar preencher número do participante como sala.
- Tela de conferência aumentada e reorganizada para contatos e participantes.
- Originate AMI agora tenta primeiro enviar para a extensão da sala e, se falhar, usa fallback direto no Application=ConfBridge.
- Log AMI/SIP corrigido: sip_signal_debug.log volta a ser gravado e também replica eventos importantes no ui_flow_debug.log.
- Em caso de falha, o log agora mostra o detalhe real do AMI em LastConferenceError.

Se o participante ainda não chamar, verificar no log: CONF AMI ORIGINATE EXTEN / CONF AMI ORIGINATE APP.
