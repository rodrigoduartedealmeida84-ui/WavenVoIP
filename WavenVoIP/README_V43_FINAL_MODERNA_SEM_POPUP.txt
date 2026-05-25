Waven VoIP - V43 Moderna sem popup

Correções aplicadas:
- Removido fluxo com ShowDialog/DialogResult da seleção de saída.
- Seleção de saída agora abre embutida na própria tela principal, evitando travamentos de janela.
- Corrigido erro CS0103 do método AbrirSeletorSaida.
- Corrigido botão ligar de Contatos e Histórico.
- Corrigida chamada para adicionar participante externo.
- Adicionado fechamento automático da tela de chamada quando o Issabel/cliente encerra a ligação.
- Mantido tratamento de CANCEL do Issabel: se outro atendente atender ou cliente desligar antes do atendimento, o popup para de tocar neste computador.
- Mantidos canais: Operadora, 0800, WhatsApp TIM e WhatsApp Vivo.

Observação:
O sincronismo instantâneo depende do Issabel/Asterisk enviar CANCEL/BYE corretamente para o ramal. Esta versão já trata esses eventos SIP no transporte e no UserAgent.
