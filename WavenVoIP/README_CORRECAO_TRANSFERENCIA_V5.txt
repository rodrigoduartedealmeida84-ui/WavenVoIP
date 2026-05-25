Correção V5 - Transferência Issabel/Asterisk

Ajustes feitos:
1. Corrigido envio DTMF RFC2833: * agora usa código 10 e # usa código 11.
   Antes o sistema podia enviar ASCII, e o Issabel não reconhecia *2/## durante chamada.
2. Transferência assistida: envia *2, aguarda, depois envia o ramal.
3. Transferência cega: envia ##, aguarda, depois envia o ramal.
4. Tela de transferência aumentada, redimensionável e com rolagem para não cortar botões.
5. Pausas DTMF aumentadas para melhorar compatibilidade com Asterisk/Issabel.

Importante no Issabel:
- Em PBX > Configurações do PBX > Opções avançadas, confirme que:
  Transferência assistida durante chamada = *2
  Transferência cega durante chamada = ##
- No ramal SIP, deixe DTMF como RFC2833/Auto quando possível.
