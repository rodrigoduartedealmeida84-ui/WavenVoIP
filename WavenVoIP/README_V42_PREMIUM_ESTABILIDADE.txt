V42 - Correção premium de estabilidade
- RouteSelectorWindow agora abre de forma segura sem ShowDialog, evitando Win32Exception de identificador inválido.
- Botões Ligar de Contatos e Histórico voltam a iniciar a chamada após escolher a saída.
- WhatsApp/Configurações/janelas auxiliares agora abrem sem modal problemático, evitando travamentos.
- InputPromptWindow não usa DialogResult diretamente; mantém confirmação por propriedade interna.
