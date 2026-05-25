WavenVoIP - V38 Correção geral de estabilidade

Correções aplicadas:
- Corrigido erro System.NullReferenceException em gridHistoricoShell.
- Histórico não é mais atualizado antes da tela terminar de carregar.
- Contatos e histórico agora possuem proteção contra controles ainda não inicializados.
- Eventos de busca/filtro não quebram mais durante InitializeComponent.
- _sipService agora é definido antes da configuração geral da tela.
- Carregamento inicial movido para o evento Loaded, deixando o WPF criar todos os controles primeiro.

Motivo do erro:
O ComboBox de filtro do histórico disparava SelectionChanged durante o carregamento do XAML, antes do ListView gridHistoricoShell existir na tela.
