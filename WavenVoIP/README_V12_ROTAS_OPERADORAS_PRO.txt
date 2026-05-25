WavenVoIP v12 - Rotas e origem PRO

Correções aplicadas:
- Histórico: ao clicar em Ligar, remove automaticamente o primeiro dígito de rota salvo no histórico.
  Exemplo: 266984671226 -> 66984671226 e só depois aplica a saída escolhida.
- Regra oficial de saídas:
  1 = Operadora
  2 = WhatsApp TIM
  3 = WhatsApp Vivo
- Histórico: coluna Origem/Saída mostra a saída escolhida nas chamadas realizadas.
- Chamadas recebidas: o app tenta identificar automaticamente a origem via cabeçalhos SIP do Issabel/Asterisk.
  Se o tronco/rota vier como TIM/VIVO/WhatsApp nos headers, será exibido como WhatsApp TIM ou WhatsApp Vivo.
  Se não vier essa informação no SIP, fica como Entrada Issabel.
