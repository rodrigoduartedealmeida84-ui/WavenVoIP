Waven VoIP - V24

Ajustes feitos:

1) Tela de discagem
- Teclado reduzido para caber melhor na janela.
- Campo de número central mantido para mostrar o que está sendo digitado.
- Botão de ligar central estilo iPhone.
- Botão de apagar ao lado do botão de ligar.
- Menos margem vertical para evitar corte do teclado.

2) Chamada recebida / popup tocando após outro usuário atender
- O sistema agora trata CANCEL recebido do Issabel/Asterisk e fecha o popup/toque.
- O botão X do popup agora apenas ignora/silencia a chamada neste computador e evita reabrir a mesma chamada.
- O botão Recusar envia Busy Here apenas para este ramal. Em fila/ring group do Issabel, os outros usuários continuam tocando.
- O sistema guarda o Call-ID recusado/ignorado para não reabrir o popup em retransmissões do Issabel.

Observação:
Se ainda tocar após outro usuário atender, verificar no Issabel/Asterisk se a fila/ring group está enviando CANCEL corretamente para os ramais que não atenderam.
