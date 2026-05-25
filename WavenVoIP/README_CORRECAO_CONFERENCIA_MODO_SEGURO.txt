Correção conferência modo seguro

Problema identificado no diagnóstico:
- O participante era originado para a sala, mas a chamada principal era redirecionada para a conferência antes de confirmar que o participante atendeu.
- Quando a rota externa falhava ou tocava mensagem de erro, o Asterisk derrubava o bridge inteiro, causando queda de todas as ligações.

Mudanças desta versão:
- Adicionar participante NÃO move mais a chamada atual automaticamente.
- O participante é chamado para a sala ConfBridge em modo seguro.
- A chamada principal continua normal até o operador clicar em "Unir chamada atual à sala".
- Adicionado botão "Unir chamada atual à sala" na janela de conferência.
- Botão Remover agora tenta desligar somente o participante via AMI Hangup, sem encerrar a chamada principal.
- Logs novos:
  CONF AMI ORIGINATE_APP_SEGURO
  CONF AMI JOIN_CALL
  CONF AMI JOIN REDIRECT_ATOMICO
  CONF AMI HANGUP

Observação:
Se o participante ouvir mensagem em inglês ou tu-tu-tu, é falha/negação da rota usada para esse número. Teste esse número fora da conferência pela mesma saída.
