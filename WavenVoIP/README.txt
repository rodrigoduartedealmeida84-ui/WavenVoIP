Substitua somente:
- src/WavenVoIP/SipService.cs

Correção desta versão:
- removido o trecho que causava o erro CS1593 no evento ServerCallCancelled
- mantida a limpeza de estado ao desligar e ao encerrar chamada
