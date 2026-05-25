VERSÃO COM PORTA DINÂMICA + RENOVAÇÃO DE REGISTER

Substitua por cima do projeto atual.

Arquivo principal alterado:
- src/WavenVoIP/SipService.cs

O que mudou:
- canal UDP local agora usa porta dinâmica (0)
- timer de renovação periódica do REGISTER
- envio de OPTIONS como keepalive leve
- mantém compatibilidade com a UI atual

Depois:
1. Limpar Solução
2. Recompilar Solução
3. Logar no ramal
4. No Asterisk CLI, conferir:
   sip show peer 104

Meta:
- sair de UNREACHABLE para OK
