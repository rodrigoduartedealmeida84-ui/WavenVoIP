Waven VoIP - V41 Premium Correções de janelas e chamadas

Correções aplicadas:
- Corrigido erro System.InvalidOperationException: DialogResult somente pode ser definido após Window ser criado e exibido como caixa de diálogo.
- RouteSelectorWindow não depende mais de DialogResult para retornar a saída escolhida.
- Tela de escolha de saída agora retorna pela propriedade Confirmado/SaidaSelecionada.
- Transferência cega, transferência assistida e conferência usam abertura segura do prompt.
- InputPromptWindow protegido contra erro de DialogResult fora de ShowDialog.
- Botões Ligar em Contatos e Histórico fecham a janela modal antes de abrir seleção de saída/tela de chamada, evitando travamento.
- Botões WhatsApp em Contatos e Histórico também fecham a janela modal antes de abrir a tela de envio.

Observação:
Esta versão mantém o layout premium e os canais do Issabel já configurados.
