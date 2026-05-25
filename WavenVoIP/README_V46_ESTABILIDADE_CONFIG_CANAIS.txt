Versão v46 - estabilidade, configuração avançada e canais Issabel

Ajustes incluídos:
- Tratamento global para Win32Exception "O identificador da janela é inválido" para não derrubar o app durante/ao encerrar chamadas.
- Fechamento seguro da tela de chamada para evitar dupla chamada de Close/ShowDialog e travamentos.
- Botão Mudo/Com áudio com ícone alternando entre alto-falante com X e alto-falante com áudio.
- Configurações agora ficam na própria aba do app, com barra de rolagem.
- Campos de conta SIP/Issabel, limpeza do histórico e mapeamento dos canais de entrada.
- Mapeamento configurável dos canais:
  Operadora: 6631998716;IN-BRDID-6631998716
  0800: 08001901900;VONO-0800-ENTRADA
  WhatsApp TIM: 556684263277;WAVOIP-556684263277
  WhatsApp Vivo: 556696308630;WAVOIP-556696308630

Observação importante:
Para o histórico mostrar exatamente Operadora/0800/WhatsApp TIM/WhatsApp Vivo em chamadas recebidas, o Issabel precisa encaminhar o DID/DDR ou nome da rota no INVITE SIP para o ramal. Se o PBX não enviar esse dado, o app mostra "Entrada não identificada" em vez de chutar errado.
