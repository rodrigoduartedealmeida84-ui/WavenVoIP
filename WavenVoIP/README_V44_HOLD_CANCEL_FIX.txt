WavenVoIP FINAL PREMIUM v44

Correções desta versão:
- Botão Segurar/Retomar corrigido: ao pausar volta corretamente para o estado Segurar.
- Fechamento pelo X da tela de chamada também envia desligamento/cancelamento.
- Se iniciar uma ligação e desligar antes do destinatário atender, o app tenta enviar CANCEL/Hangup imediatamente para evitar chamada muda.
- Tela de chamada fecha por sistema sem reenviar desligamento duplicado.
- Fluxo de chamada sainte protegido contra retorno tardio após cancelamento.

Observação técnica:
A versão tenta diferentes métodos do SIPSorcery por reflexão (Cancel, CancelCall, CancelInvite, Hangup, HangupCall e Reject) para ser compatível com versões diferentes da biblioteca.
