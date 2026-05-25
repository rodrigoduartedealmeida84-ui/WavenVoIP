V45 - estabilidade em chamada

Correções aplicadas:
- Tratamento global para Win32Exception de identificador de janela inválido, evitando queda do app/ligação por exceção do WPF.
- Botão Segurar/Retomar corrigido para alternar o ícone e texto corretamente.
- Segurar/Retomar não deixa mais a interface presa em símbolo incorreto.
- Desligar foi protegido para cancelar chamada em andamento sem derrubar a interface.
- Chamada sainte cancelada antes do atendimento passa por rotina segura de cancelamento/hangup.
