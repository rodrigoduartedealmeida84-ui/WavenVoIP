Waven VoIP - V8

Correções aplicadas:
- Botão Adicionar não usa mais transferência assistida (*2).
- Adicionar agora chama um novo participante para conferência local pelo app.
- Transferência cega/assistida limitada a ramais internos.
- Ao iniciar chamada, toca ringback local tipo celular (tuuu... tuuu...) até atender/falhar.
- Botão Contatos dentro da chamada foi removido/substituído por área de conferência.

Observação técnica:
A conferência local depende de suporte a múltiplas sessões SIP/áudio simultâneas na máquina e no driver de áudio. Se o áudio dos participantes não misturar corretamente, o próximo passo é implementar um mixer NAudio dedicado.
