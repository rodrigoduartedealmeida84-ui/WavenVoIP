Waven VoIP - versão final estável (base)

O que esta versão faz:
- força REGISTER e INVITE para o servidor SIP em 191.252.202.208:5061 via UDP
- mantém porta local fixa em 5060 para facilitar retorno/NAT
- envia keep-alive periódico
- tenta manter o ramal online
- trata chamada de saída e recebimento básico
- discagem de 3 dígitos vai direto como chamada interna

Importante:
- esta é uma base estável de integração, mas ainda pode exigir ajuste fino conforme o comportamento do seu Issabel/chan_sip
- substitua os arquivos no projeto e recompile
