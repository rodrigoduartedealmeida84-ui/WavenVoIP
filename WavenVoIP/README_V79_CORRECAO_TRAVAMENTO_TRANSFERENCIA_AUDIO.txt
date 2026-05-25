Correção aplicada:
- Revertida a tela InputPromptWindow.xaml para o layout estável anterior.
- Essa tela é usada por transferência e conferência para informar destino.
- Objetivo: remover o travamento causado pela versão visual pesada da lista de contatos.
- Fluxo de chamada/SIP não foi alterado.

Observação sobre áudio cortando:
- O áudio pode cortar quando a UI trava ou consome CPU durante a chamada.
- Esta correção reduz esse risco removendo a tela pesada.
- Se continuar cortando, enviar diagnóstico com horário exato da chamada.
