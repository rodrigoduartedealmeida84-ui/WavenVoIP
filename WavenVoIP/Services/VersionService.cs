using System;

namespace WavenVoIP.Services
{
    public static class VersionService
    {
        public const string Versao  = "2.4.3";
        public const string NomeApp = "WavenVoIP";

        public static readonly DateTime DataBuild = new DateTime(2026, 8, 15);

        public static string VersaoCompleta => $"{NomeApp} v{Versao}";
        public static string VersaoComData  => $"{NomeApp} v{Versao}  •  build {DataBuild:dd/MM/yyyy}";

        public static string Changelog =>
            "v2.4.3 — Correcao de vazamento de memoria gerenciado (15/08/2026)\n" +
            "• Correcao de vazamento de memoria confirmado em producao (fila interna de log sem limite)\n" +
            "• Fila de log agora tem capacidade maxima — nao pode mais crescer sem limite\n" +
            "• Reducao de volume de log repetitivo na sincronizacao de CDR (mesma logica, so menos linhas)\n" +
            "• Preserva integralmente as correcoes de audio e favoritos da v2.4.1/v2.4.2 e o Diagnostico Remoto da v2.4.2\n" +
            "• [SAFE] Nenhuma alteracao em SIP, discagem, regras de CDR, Issabel, BRDID, AMI, dialplan, Google Contacts ou Favoritos\n" +
            "\n" +
            "v2.4.2 — Diagnostico remoto (15/08/2026)\n" +
            "• Diagnostico remoto de desempenho e estabilidade\n" +
            "• Monitoramento de memoria, CPU, handles, threads e I/O\n" +
            "• Correlacao de consumo com volume de chamadas\n" +
            "• Monitoramento remoto de incidentes\n" +
            "• Preserva as correcoes de memoria e favoritos da v2.4.1\n" +
            "• [SAFE] Nenhuma alteracao em SIP, discagem, CDR, Issabel, BRDID, AMI, dialplan ou "+
            "Google Contacts\n" +
            "\n" +
            "v2.4.1 — Memoria e favoritos (14/08/2026)\n" +
            "• Correcao de consumo excessivo de memoria durante uso prolongado e muitas chamadas\n" +
            "• Melhorias na liberacao dos recursos de audio apos o encerramento das ligacoes\n" +
            "• Correcao de favoritos que podiam desaparecer durante a sincronizacao\n" +
            "• Melhorias na identificacao de favoritos com numeros contendo 9o digito, 55 ou +55\n" +
            "• Melhorias de estabilidade\n" +
            "• [SAFE] Nenhuma alteracao em SIP, discagem, CDR, Issabel, BRDID, AMI, dialplan ou "+
            "Google Contacts\n" +
            "\n" +
            "v2.4.0 — Correcao do 9o digito em celulares + desempenho e estabilidade (12/08/2026)\n" +
            "• [FIX-CRITICO] Celular sem o 9o digito (formato antigo, comum em contatos salvos "+
            "antes de 2016, retorno de chamada do Historico/Favoritos/WhatsApp) nao completava a "+
            "ligacao ao discar pela Operadora — a normalizacao que adiciona o 9o digito ja existia "+
            "no app (usada para identificar/casar contatos), mas nao era aplicada no ponto onde o "+
            "prefixo de rota (Operadora/WhatsApp TIM/WhatsApp Vivo) e montado antes de discar, nem "+
            "no envio do template do WhatsApp\n" +
            "• [FIX] DialPlanService.AplicarRegraDeDiscagem (usado por Discador, Historico, "+
            "Contatos e Favoritos) agora normaliza o numero — adiciona o 9o digito somente quando "+
            "for celular (DDD + 8 digitos comecando com 6-9), nunca em fixo — antes de aplicar o "+
            "prefixo de rota. Normalizacao e idempotente: numero que ja tem 9 ou e fixo passa "+
            "intacto\n" +
            "• [FIX] WhatsAppService.NormalizarTelefoneParaEnvio (template iniciar_conversa) e a "+
            "tela de abrir conversa no WhatsApp agora usam a mesma normalizacao central antes de "+
            "enviar/exibir o numero\n" +
            "• [FIX] Busca de nome de contato ao abrir o WhatsApp a partir de Historico/Contatos "+
            "passou a reconhecer como o mesmo contato tanto o numero salvo com quanto sem o 9o "+
            "digito (reaproveitando o mesmo casamento ja usado no restante do app)\n" +
            "• [SAFE] Nenhum contato existente foi alterado em massa — um contato antigo salvo sem "+
            "o 9 continua exatamente como esta; a normalizacao passa a ser aplicada apenas no "+
            "momento de discar/enviar\n" +
            "• [SAFE] Nenhuma alteracao em SIP, RTP, codecs, Issabel, BRDID, AMI, dialplan, filas "+
            "ou logica de encerramento de chamadas\n" +
            "• [PERF] Criacao de midia/enumeracao de dispositivo de audio nao bloqueia mais "+
            "indefinidamente a interface ao iniciar ou atender uma ligacao — agora tem timeout, "+
            "com fallback pro dispositivo padrao se a busca demorar demais\n" +
            "• [FIX] Guards contra sincronizacoes concorrentes de AMI e de Google Contacts "+
            "(evita empilhar tentativas quando uma sincronizacao anterior ainda esta rodando)\n" +
            "• [FIX] Sincronizacao com Google Contacts agora respeita corretamente o "+
            "backoff/cooldown em caso de falha temporaria da API, em vez de tentar de novo em "+
            "paralelo\n" +
            "• [PERF] Otimizacao do processamento de duplicatas do Historico/CDR (removida "+
            "recomputacao redundante que rodava a cada ciclo de sincronizacao)\n" +
            "• [PERF] Reducao de logs redundantes gerados pela sincronizacao de CDR\n" +
            "• [FIX] Leitura de contato durante sincronizacao AMI nao fica mais presa junto com "+
            "o bloqueio de memoria compartilhada\n" +
            "• [DIAG] Novo monitoramento leve interno para detectar e registrar qualquer travamento "+
            "futuro da interface, e registro de exceções de tarefas em segundo plano que antes "+
            "eram descartadas silenciosamente\n" +
            "\n" +
            "v2.3.7 — Reducao de consumo de CPU ocioso (10/08/2026)\n" +
            "• [PERF] Investigacao real (instrumentacao temporaria) confirmou CPU ociosa oscilando "+
            "46-167% mesmo sem nenhuma chamada em andamento — causa dominante: os timers de "+
            "sincronizacao (CDR/Historico, Google Contatos, AMI) rodando no intervalo padrao de 3s, "+
            "continuamente, mesmo sem nada novo pra sincronizar\n" +
            "• [PERF] SipConfig.CarregarSalva() (leitura+parse de sipconfig.json do disco) media "+
            "~4-5 chamadas/segundo em regime permanente, custando ~10-20ms de CPU a cada 5s — "+
            "implementado cache em memoria thread-safe, invalidado automaticamente a cada Salvar(), "+
            "sempre devolvendo uma copia independente pra cada chamador (nunca a mesma instancia "+
            "compartilhada — sem risco de mutacao acidental entre os ~55 pontos de chamada). "+
            "Custo por chamada caiu de ~0,5ms para ~0,002ms\n" +
            "• [LOG] CDR_RECORDING_CORRECTS_NUMBER (calculo deterministico repetido em todo ciclo de "+
            "sync, ~78% do volume do cdr_sync.log numa sessao de teste) movido para o toggle "+
            "'Logs detalhados', igual as demais linhas de diagnostico repetitivo — nenhum log "+
            "critico (Cancelada/Recusada/Nao atendida/erros) foi afetado\n" +
            "• [PERF] Resultado medido: CPU ociosa media caiu de ~74% para ~56% (-24%) na mesma "+
            "maquina/config, sem regressao de memoria/threads/handles (testado com 180+ ciclos de "+
            "chamada e validado em uso real)\n" +
            "• [SAFE] Nenhuma alteracao em timers/intervalos, sincronizacao de CDR, Reprocessar, "+
            "classificacao de chamadas (Realizada/Recebida/Perdida/Recusada/Nao atendida/Cancelada), "+
            "SIP CANCEL/BYE, Historico, filas, canais, popups ou WhatsApp — mudanca isolada ao "+
            "carregamento de configuracao e a um log nao-critico\n" +
            "\n" +
            "v2.3.6 — Correcao critica de cancelamento antecipado + Canal externo real no Historico (10/08/2026)\n" +
            "• [FIX-CRITICO] Cancelar uma chamada de saida ANTES do cliente atender podia deixar o telefone do cliente tocando mesmo com o Waven ja mostrando \"Cancelada\" — causa raiz confirmada com log real de producao: SIPUserAgent.Cancel() (SIPSorcery) dispara o retorno de \"cancelado\" IMEDIATAMENTE e LOCALMENTE, sem esperar nenhuma confirmacao de rede do Asterisk. O Waven ja desmontava a chamada (midia, estado, UserAgent livre) achando que o CANCEL tinha sido entregue, sem prova nenhuma disso\n" +
            "• [FIX] Ligar()/Desligar() agora usam um lock dedicado pra fechar a corrida entre clicar Ligar e clicar Desligar quase juntos: se o cancelamento chegar ANTES do INVITE ser efetivamente enviado, o INVITE nunca sai (OUTBOUND_INVITE_SUPPRESSED_BY_CANCEL) — nunca mais uma tentativa cancelada localmente continua tocando no cliente\n" +
            "• [NEW] Prova real de entrega do CANCEL: handlers penduram nos eventos da transacao SIP de verdade (nao so no retorno local) e logam a resposta que o Asterisk efetivamente mandou (OUTBOUND_SIP_CANCEL_SENT/RESPONSE, OUTBOUND_INVITE_TERMINATED)\n" +
            "• [NEW] Reforco automatico: se nenhuma confirmacao de rede chegar em ~600ms, o CANCEL e reenviado uma vez sozinho, em segundo plano, sem travar a interface\n" +
            "• [SAFE] Cancelar DURANTE o toque (Ringing) continua funcionando exatamente como antes; chamada atendida continua encerrando com BYE normal — nada disso foi alterado\n" +
            "• [PERF] Testado sob estresse local (180+ ciclos Ligar/Cancelar/Desligar, incluindo corrida real entre threads): sem vazamento de memoria, sem crescimento de threads/handles, sem excecoes\n" +
            "• [FIX] Causa raiz identificada com CDR real de producao: quando a ligacao passa por URA/fila antes de chegar num agente, o registro CDR \"principal\" escolhido para classificacao e a perna Local/<ramal>@from-queue do agente — sem nenhuma informacao de tronco. DedurzirTronco via o Channel Local/ e devolvia \"Queue\" (nao vazio), bloqueando a deteccao do canal real pelo nome do arquivo de gravacao (guardada por checagem de origemSaida vazia) — o Historico so mostrava \"URA\", escondendo o canal externo verdadeiro\n" +
            "• [NEW] HistoricoLigacaoItem.CanalEntrada: canal EXTERNO real (Operadora/0800/WhatsApp TIM/WhatsApp Vivo), resolvido em paralelo a OrigemSaida — nao quebra os rotulos de fluxo interno ja validados (\"Abandonada na fila\"/\"Desligou antes da fila\")\n" +
            "• [NEW] IssabelCdrService.IdentificarCanalExternoDoGrupo: quando o nome da gravacao nao resolve o canal, busca em QUALQUER linha do grupo CDR (nao so a principal) por um Channel/DstChannel que identifique o tronco de entrada\n" +
            "• [UI] Historico: quando o canal externo e conhecido, o badge mostra o canal real (ex.: \"0800\", \"Operadora\") em vez de \"URA\" — exceto abandono de fila, que continua mostrando \"Abandonada na fila\"/\"Desligou antes da fila\"\n" +
            "• [NEW] Historico > botao Ligar (exclusivo desta tela — Contatos/Favoritos/Discador inalterados): em chamada Recebida/Perdida com canal externo conhecido, pergunta \"Deseja retornar pelo mesmo canal?\" antes de discar, com opcao de escolher outro canal (reutiliza o seletor existente)\n" +
            "• [NEW] Se o canal original nao tiver rota de saida equivalente (ex.: 0800 e canal so de ENTRADA — nao existe prefixo de discagem para ele), mostra aviso e abre o seletor normal automaticamente\n" +
            "• [SAFE] Registros antigos sem CanalEntrada continuam abrindo o seletor normal, sem perguntar nada\n" +
            "• [SAFE] Nenhuma alteracao em Contatos, Favoritos, Discador, Dashboard, audio, RTP, filas, AMI, WhatsApp, Auto Start ou nas correcoes da v2.3.5 (Recusada/Nao atendida/agrupamento de CDR)\n" +
            "\n" +
            "v2.3.5 — Correcao de chamadas Realizadas nunca viram Perdida + status Recusada/Nao atendida (08/08/2026)\n" +
            "• [FIX] Chamada REALIZADA (operador ligou) que nao completa nunca mais aparece como \"Perdida\" no Historico — Perdida volta a ser exclusivo de chamadas RECEBIDAS sem atendimento\n" +
            "• [FIX] Causa raiz identificada com dados reais de producao: o tronco de saida (Operadora/WhatsApp TIM/Vivo) substitui o Caller-ID pelo proprio DID da rota (pratica padrao de telefonia) — a deteccao de direcao (ClassificarChamada) so olhava cdr.Src, e nao detectava o ramal quando isso acontecia\n" +
            "• [FIX] Numero da propria empresa (DID de tronco) nao aparece mais como destino de uma chamada Perdida — pernas auxiliares de retentativa de rota que nunca conectam e vazam nosso proprio DID sao detectadas e suprimidas (SELF_NUMBER_LEG_DETECTED/SUPPRESSED)\n" +
            "• [NEW] Novo status Recusada — chamada REALIZADA rejeitada rapidamente pelo cliente\n" +
            "• [NEW] Novo status Nao atendida — chamada REALIZADA que tocou ate acabar sem resposta\n" +
            "• [FIX] Investigacao com testes reais provou que a operadora/WAVOIP devolve o MESMO codigo (486 Busy Here no SIP, disposition BUSY no CDR) tanto para recusa real quanto para timeout de toque — nao ha campo que diferencie os dois. Adotada heuristica por tempo: BUSY com menos de 30s desde o Ringing = Recusada; 30s ou mais = Nao atendida (limiar centralizado em SipService.OutboundDeclineThresholdSeconds, ajustavel). So se aplica com Ringing confirmado — falha rapida sem Ringing (erro de rota, 404, 5xx) nunca vira Recusada\n" +
            "• [NEW] Toast \"Chamada recusada\" (OutboundDeclinedToast) no canto inferior direito, com nome/numero do cliente — fecha sozinho em 6s ou pelo X. Nunca reutiliza o popup de chamada perdida (exclusivo de chamadas recebidas). So aparece quando o resultado final e Recusada, nunca para Nao atendida\n" +
            "• [FIX] Resultado classificado ao vivo pelo SipService (tempo real de Ringing->BusyHere) e preservado quando o CDR sincroniza depois — o CDR so pode CONFIRMAR o resultado ao vivo, nunca rebaixa-lo (ex.: SIP classificou Nao atendida por timeout, CDR chega com BUSY generico — mantem Nao atendida)\n" +
            "• [FIX] Corrigido: nome do arquivo de gravacao (force-record) e criado pelo Asterisk assim que o Dial() comeca, mesmo em chamadas BUSY/sem resposta — o codigo antigo usava a existencia desse arquivo para forcar tipo=Realizada, fazendo uma chamada Recusada/Nao atendida \"virar\" Realizada assim que o CDR sincronizava. Removida essa sobrescrita indevida\n" +
            "• [FIX] Corrigido bug mais profundo achado com 4 discagens manuais reais para o mesmo numero: MergeGruposPorSrcJanela (feito pra unir pernas de UMA MESMA chamada de fila/ring-group) estava fundindo discagens MANUAIS independentes pro mesmo numero dentro de 180s, porque o numero com prefixo de rota e identico entre tentativas. Isso fazia ate 4 cliques reais virarem 1 registro so (sempre o mais recente), com as outras tentativas descartadas, duracao de toque herdada de OUTRA tentativa (Recusada virando Nao atendida por engano) e o UniqueId principal trocando a cada nova discagem. Nova funcao PodeMesclarGruposPorJanela bloqueia a fusao quando os dois grupos sao discagens outbound diretas (dcontext=from-internal originadas por um ramal) — cada clique do operador mantem seu proprio linkedid, isolado, sem fundir. Fila/ring-group/URA continuam fundindo normalmente (dcontext diferente)\n" +
            "• [FIX] Duracao de toque usada na heuristica Recusada/Nao atendida agora so soma as pernas do MESMO linkedid do registro escolhido — nunca do grupo inteiro (evita herdar duracao de outra tentativa mesmo em grupos legitimamente fundidos, como fila)\n" +
            "• [FIX] Reconciliacao SIP↔CDR (item anterior) refinada: janela estreita de 45s (cobre a defasagem de relogio cliente/servidor observada, ~15s, sem invadir o intervalo entre discagens manuais) com desempate por ramal originador e rota antes de cair pra janela ampla de 120s; se sobrar mais de um candidato, nao substitui (mantem o valor que o proprio CDR calculou, ja confiavel com o merge corrigido)\n" +
            "• [UI] Historico: cores e icones proprios para Recusada (vermelho) e Nao atendida (amarelo/laranja); filtros \"Recusadas\" e \"Nao atendidas\" adicionados\n" +
            "• [UI] Status \"Nao atendida\" (chamada recebida atendida por outro ramal) renomeado para \"Atendida em outro ramal\" — evita confusao com o novo status de chamada realizada\n" +
            "• [FIX] Direcao da chamada (ehOrigem) agora tambem reconhece o ramal pelo campo channel do CDR (SIP/104-...), nao so pelo Caller-ID em cdr.Src\n" +
            "• [LOG] CALL_CLASSIFY_START/GROUP/DIRECTION/FINAL, OUTBOUND_RING_DURATION, OUTBOUND_BUSY_CLASSIFICATION, OUTBOUND_CLASSIFIED_DECLINED/NO_ANSWER, OUTBOUND_RESULT_CONFLICT/SIP_PRESERVED/CDR_APPLIED, SELF_NUMBER_LEG_DETECTED/SUPPRESSED, CDR_MAIN_LEG_SELECTED, OUTBOUND_DECLINED_TOAST_SHOW/AUTO_CLOSE/MANUAL_CLOSE\n" +
            "• [SAFE] Preservado integralmente: chamada recebida atendida por outro operador, abandono de fila, deduplicacao, refresh 2s/5s, notificacoes de chamada perdida — nenhuma mudanca em audio, RTP, codecs, registro SIP, filas do Issabel, Waven API, contatos, favoritos, WhatsApp ou Auto Start\n" +
            "\n" +
            "v2.3.4 — Layout dos Favoritos + nome sincronizado com Contatos (30/07/2026)\n" +
            "• [FIX] Favoritos: coluna de nome recebeu mais espaco — nomes longos nao sao mais cortados apos a inclusao do botao WhatsApp na v2.3.3\n" +
            "• [FIX] Nome do favorito agora e resolvido a partir do contato atual (vinculo por ContactId, com fallback por telefone normalizado) — editar o nome de um contato atualiza automaticamente todos os favoritos correspondentes\n" +
            "• [FIX] Atualizacao do nome chega a todos os ramais no proximo ciclo de sincronizacao com a Waven API (a cada 60s), sem precisar remover e favoritar novamente\n" +
            "• [NEW] Migracao automatica e segura de favoritos antigos (sem vinculo) por telefone normalizado, na primeira execucao desta versao\n" +
            "• [FIX] Publish-Clean.ps1: corrigido round-trip do version.json que podia corromper acentos, cedilha e travessoes (mojibake) — leitura/escrita agora usam UTF-8 sem BOM de forma explicita\n" +
            "• [LOG] FAVORITE_CONTACT_LINK_BY_ID / FAVORITE_CONTACT_LINK_BY_PHONE / FAVORITE_CONTACT_NAME_UPDATED / FAVORITE_CONTACT_NOT_FOUND / FAVORITES_UI_REFRESHED\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, chamadas, historico, filas, AMI, Issabel, Auto Start, login, atualizador, template iniciar_conversa ou botao WhatsApp\n" +
            "\n" +
            "v2.3.3 — WhatsApp em Favoritos + novo template iniciar_conversa (28/07/2026)\n" +
            "• [UI] WhatsApp adicionado a lista de Favoritos — mesmo icone, cor e acao ja usados em Contatos\n" +
            "• [UI] Favoritos agora possuem a mesma experiencia da lista de Contatos\n" +
            "• [NEW] Novo template padrao WABA 'iniciar_conversa' (MARKETING, pt_BR, botao QUICK_REPLY 'Iniciar Conversa')\n" +
            "• [UI] Interface do preview do template modernizada, com previa da mensagem estilo celular\n" +
            "• [UI] Melhorias visuais e pequenos ajustes de interface\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, chamadas, historico, contatos, dashboard, " +
            "Auto Start, login, atualizador, Waven API, AMI, Issabel ou regras de filas\n" +
            "\n" +
            "v2.3.2 — Reativacao unica do inicio com o Windows para todos os usuarios (20/07/2026)\n" +
            "• [FEATURE] Na primeira execucao apos atualizar para a 2.3.2, o Waven habilita 'Iniciar " +
            "com o Windows' automaticamente para todos os usuarios — inclusive quem nunca tinha " +
            "ativado a opcao — evitando a necessidade de configurar manualmente cada computador\n" +
            "• [SAFE] Essa reativacao acontece uma UNICA vez por maquina (flag local dedicada). Depois " +
            "da migracao, a escolha do usuario volta a ser respeitada normalmente: quem desativar " +
            "'Iniciar com o Windows' depois da migracao nao tem a opcao reativada sozinha\n" +
            "• [FIX] Preservada integralmente a correcao da v2.3.1: a entrada oficial 'WavenVoIP' " +
            "nunca e' confundida com duplicatas antigas por diferenca de maiusculas/minusculas\n" +
            "• [LOG] Novo rastro de log da migracao: AUTOSTART_232_MIGRATION_CHECK/REQUIRED/ALREADY_DONE, " +
            "AUTOSTART_232_FORCE_ENABLE_START/SUCCESS/FAILED, AUTOSTART_232_FLAG_CREATED\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, chamadas, historico, contatos, " +
            "favoritos, dashboard, Waven API/Chat, Issabel ou regras de filas\n" +
            "\n" +
            "v2.3.1 — Correcao critica: autostart parou de funcionar na v2.3.0 (20/07/2026)\n" +
            "• [FIX] A rotina de autocorrecao do Auto Start (nova na v2.3.0) apagava a propria " +
            "entrada 'WavenVoIP' a cada inicio do app — a lista de nomes legados a limpar incluia " +
            "'WavenVoip', que o Windows trata como o MESMO valor que 'WavenVoIP' (Run key nao " +
            "diferencia maiusculas/minusculas). A rotina validava/criava a entrada oficial e, " +
            "no passo seguinte, apagava esse mesmo valor por engano — resultado: nenhuma entrada " +
            "sobrava no registro apos o primeiro inicio, e o Waven nunca mais abria sozinho com o Windows\n" +
            "• [FIX] Corrigido parse do caminho gravado no Run key: quando o valor tinha aspas e o " +
            "sufixo ' /autostart', o codigo antigo (Trim+Split) deixava uma aspa colada no final do " +
            "caminho extraido, fazendo File.Exists() falhar mesmo com a entrada correta\n" +
            "• [LOG] Novo rastro de log no autostart: AUTOSTART_CHECK_START, AUTOSTART_REGISTRY_ENTRY_FOUND, " +
            "AUTOSTART_REGISTRY_ENTRY_VALID/INVALID, AUTOSTART_ENTRY_UPDATED, AUTOSTART_DUPLICATE_REMOVED, " +
            "AUTOSTART_DISABLED_BY_USER, AUTOSTART_FINAL_STATE, AUTOSTART_ERROR\n" +
            "• [SAFE] Mantido: migracao de config para LocalAppData, backups rotativos, gravacao atomica " +
            "e correcao de entradas antigas/invalidas — nenhuma mudanca em SIP, audio, RTP, codecs, " +
            "chamadas, historico, contatos, dashboard, Waven API/Chat ou Issabel\n" +
            "\n" +
            "v2.3.0 — Correcao definitiva da configuracao vazia no autostart (17/07/2026)\n" +
            "• [FIX] Alguns operadores abriam o Waven na tela de Configuracao Inicial (ramal/login/senha vazios) " +
            "ao iniciar o Windows — causa raiz: config do usuario ficava em %APPDATA% (roaming), que pode ser " +
            "redirecionado para rede via GPO em maquinas de dominio; se o share nao estava disponivel no instante " +
            "exato do autostart, o app tratava isso como instalacao nova\n" +
            "• [FIX] Configuracao do usuario migrada automaticamente de %APPDATA% para %LOCALAPPDATA%\\WavenVoIP\\ " +
            "(mesma pasta ja usada por contatos, historico, favoritos e logs) — local sempre disponivel, nunca redirecionado\n" +
            "• [FIX] Migracao automatica e silenciosa do arquivo antigo para o novo caminho na primeira execucao, " +
            "sem pedir ramal/senha novamente e sem apagar o arquivo original\n" +
            "• [FIX] Carregamento resiliente: falha de leitura (arquivo corrompido, vazio ou bloqueado) agora tenta " +
            "os ultimos backups automaticos antes de concluir que nao ha configuracao — nunca mais abre a tela de " +
            "configuracao vazia por uma falha temporaria\n" +
            "• [FIX] Backup rotativo (ultimas 3 versoes) e gravacao atomica (arquivo temporario + validacao + " +
            "substituicao) — elimina o risco de um arquivo de configuracao zerado por um desligamento no meio da escrita\n" +
            "• [FIX] Autostart: entrada do Windows Run key incorreta (apontando para executavel antigo/inexistente) " +
            "e duplicatas de versoes anteriores agora sao detectadas e corrigidas automaticamente no proximo inicio\n" +
            "• [PERF] Correcoes acumuladas das versoes 2.2.x: fila/historico/notificacao mais rapidos, sincronizacao AMI\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, chamadas, historico, contatos, dashboard, Waven API/Chat ou Issabel\n" +
            "\n" +
            "v2.2.4 — Abandono na fila, Historico e notificacao mais rapidos (11/07/2026)\n" +
            "• [FIX] Chamada abandonada na fila que tocou no ramal do agente (em qualquer ciclo) agora " +
            "e' sempre importada no Historico — antes, o registro CDR principal escolhido para chamadas " +
            "de fila sem atendimento era a perna de entrada da fila (sem referencia ao ramal do agente), " +
            "fazendo a chamada ser descartada como \"nao e' do meu ramal\" mesmo tendo tocado de verdade\n" +
            "• [FIX] Deteccao de saida da fila (atendida ou abandonada) agora e' orientada por evento " +
            "(snapshot de Filas em Tempo Real), disparando uma recheck acelerada do CDR em 3s/6s/10s " +
            "apos a saida, em vez de esperar o proximo ciclo do timer normal\n" +
            "• [PERF] Historico: refresh incremental de CDR a cada 2s com a aba aberta, 5s em segundo " +
            "plano (antes: 6s aba aberta / intervalo configurado em segundo plano)\n" +
            "• [PERF] Notificacao de chamada perdida/abandonada: chega em poucos segundos apos a saida " +
            "real da fila, em vez de aguardar o proximo ciclo de sincronizacao normal\n" +
            "• [SAFE] Nunca notifica nem registra abandono enquanto o cliente ainda estiver esperando na " +
            "fila (Filas em Tempo Real continua sendo a fonte de verdade antes de decidir)\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, credenciais, Waven API/Chat, contatos ou dashboard\n" +
            "\n" +
            "v2.2.3 — Filas, Historico e chamada perdida (11/07/2026)\n" +
            "• [FIX] Chamada em fila/ring group: ramal volta a tocar em todos os ciclos oferecidos pelo Issabel " +
            "(deteccao por chamador, nao so por Call-ID) — antes so tocava no primeiro ciclo\n" +
            "• [FIX] Chamada nao pode mais ser marcada Perdida/Abandonada enquanto o cliente ainda estiver " +
            "esperando na fila (consulta o estado ao vivo de Filas em Tempo Real antes de decidir)\n" +
            "• [FIX] Historico: chamada atendida por outro ramal nao aparece mais como \"Perdida\"/\"Abandonada na fila\" duplicada\n" +
            "• [FIX] Notificacao de chamada perdida: so dispara depois do resultado final confirmado " +
            "(nao mais so por ring/timeout local)\n" +
            "• [FIX] NaoAtendidaNesseRamal (outro ramal atendeu) nunca mais gera popup/notificacao de chamada perdida\n" +
            "• [FIX] Historico atualiza e remove duplicatas automaticamente, sem depender do botao \"Atualizar CDR\"\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, RTP, codecs, credenciais, Waven API/Chat, contatos ou dashboard\n" +
            "• [NOTA] Comportamento de ciclos de toque validado em producao apos ajuste na fila 700 do Issabel " +
            "(timeout do agente reduzido de 120s para 20s, configuracao no PBX). Alteracao pertence ao Issabel/Asterisk, " +
            "nao ao Waven — registrada aqui apenas para referencia futura\n" +
            "\n" +
            "v2.2.2 — Correcao visual do campo de discagem: numero nao cortado (29/06/2026)\n" +
            "• [FIX] Campo de discagem: numero digitado nao e mais cortado na parte inferior\n" +
            "• [FIX] Centralizacao vertical corrigida — TextBox auto-dimensiona a altura da linha e o layout centraliza no campo\n" +
            "• [SAFE] Nenhuma alteracao em SIP, audio, codecs, fluxo de chamadas ou configuracoes do usuario\n" +
            "\n" +
            "v2.2.1 — Ramais ao Vivo, discador, contatos globais, sincronizacao API, WhatsApp/Wavoip (29/06/2026)\n" +
            "• [UI] Popup de chamada recebida: botao Fechar vermelho no canto superior direito\n" +
            "• [UI] Popup de chamada perdida (MissedCallPopup) redesenhado com cabecalho colorido e botao Fechar vermelho\n" +
            "• [UI] Notificacao de chamada perdida (MissedCallToast) modernizada com icone e botao Fechar vermelho\n" +
            "• [UI] Campo de discagem: contorno interno removido — apenas borda roxa externa no foco\n" +
            "• [UI] Ramais ao Vivo: chips de filtro substituidos por dropdown — fim da sobreposicao no botao Atualizar\n" +
            "• [FIX] Performance por ramal: chamadas atribuidas ao agente que atendeu (RamalAtendeu), nao a fila/tronco\n" +
            "• [FIX] WhatsApp/Wavoip: cancel antes do ring enviava BYE via Hangup() quando chamada ja estabelecida\n" +
            "• [FIX] Historico: numero nao duplicado quando nao ha contato salvo\n" +
            "• [FIX] MissedCallPopup: numero nao exibido duas vezes quando callerName == callerNumber\n" +
            "• [FIX] MissedCallToast: timer parado corretamente ao fechar (sem memory leak)\n" +
            "• [FIX] Contatos: novos contatos agora sincronizam com a Waven API para todos os ramais\n" +
            "• [NEW] Favorito global 'Suporte Dlink Sistemas' (3832010900) propagado automaticamente via seed\n" +
            "• [LOG] Logs de diagnostico adicionados: CONTACTS_LOAD_OK, CONTACT_SAVE_OK, FAVORITE_SAVE_OK e outros\n" +
            "• [SAFE] Nenhuma mudanca em SIP, audio, codecs, registro de ramal ou configuracoes do usuario\n" +
            "\n" +
            "v2.2.0 — Melhorias de desempenho, fluidez, inicializacao, pesquisa, timers, logs e reducao de travamentos (24/06/2026)\n" +
            "• [PERF] Logs gravados em fila assincrona por thread dedicada — chamadas a LogHelper (UI thread, cliques, eventos SIP) nao bloqueiam mais em I/O de disco\n" +
            "• [FIX] Corrigido memory/CPU leak: contador de duracao do CallWindow continuava rodando a cada 1s apos a janela ser fechada\n" +
            "• [PERF] Removida leitura/regravacao duplicada do historico.json na thread de UI durante a inicializacao (retencao ja era aplicada em background)\n" +
            "• [PERF] Virtualizacao explicita (Recycling) nas listas de contatos e historico da tela principal\n" +
            "• [SAFE] Nenhuma mudanca em SIP, audio, fluxo de chamadas ou configuracoes do usuario\n" +
            "\n" +
            "v2.1.1 — Correcao Online/Offline + icone no autostart (24/06/2026)\n" +
            "• [FIX] Online/Offline agora e controlado localmente pelo WavenVoIP — nao depende mais de discar *78/*79 no Issabel\n" +
            "• [FIX] Ao voltar Online, o app forca verificacao/renovacao do registro SIP antes de mostrar o ramal pronto\n" +
            "• [NEW] Estado Offline persiste apos fechar ou reiniciar o Windows — app abre Offline se foi o ultimo estado salvo\n" +
            "• [SAFE] Chamada recebida enquanto Offline e rejeitada de forma limpa (486 Busy), sem tocar localmente\n" +
            "• [FIX] Icone/logo da janela principal carregado via caminho absoluto, corrigindo icone em branco ao iniciar com o Windows\n" +
            "• [FIX] WorkingDirectory corrigido no startup automatico do Windows (Run key herdava diretorio do explorer.exe)\n" +
            "• [LOG] USUARIO_CLICOU_ONLINE | USUARIO_CLICOU_OFFLINE | OFFLINE_ATIVADO_LOCAL | RAMAL_PRONTO_PARA_RECEBER_CHAMADAS\n" +
            "• [LOG] APP_INICIADO_OFFLINE_POR_CONFIG_SALVA | CALL_BLOCKED_OFFLINE | FALHA_AO_VOLTAR_ONLINE\n" +
            "\n" +
            "v2.0.0 — Nova versao principal: DTMF redesenhado + pesquisa rapida + UX (22/06/2026)\n" +
            "• [NEW] Teclado DTMF totalmente redesenhado: visual moderno, botoes maiores, letras ABC/DEF sob os numeros\n" +
            "• [FIX] Display do DTMF: mostra todos os digitos pressionados (123#) — nao dependia mais do retorno do envio\n" +
            "• [NEW] Botao dedicado para limpar o display do DTMF (apenas visual, nao reenvia tons)\n" +
            "• [FIX] Layout do DTMF sem corte de numeros/letras em 100%/125%/150% de escala do Windows\n" +
            "• [SAFE] Envio automatico de DTMF na chamada mantido exatamente como antes (logica SIP inalterada)\n" +
            "• [PERF] Pesquisa de contatos e listas grandes com desempenho melhorado\n" +
            "• [NEW] Exclusao de contato com opcao Desfazer (Undo)\n" +
            "• [IMPROVE] Tratamento de contatos Google: edicao, exclusao e tombstones mais consistentes\n" +
            "• [IMPROVE] Integracao Waven API, WhatsApp WABA (template novo_atendimento) e anti-spam consolidados\n" +
            "• [STABILITY] Melhorias gerais de estabilidade e correcoes diversas\n" +
            "\n" +
            "v1.4.5 — Template WABA novo_atendimento + botao Continuar Conversa (11/06/2026)\n" +
            "• [NEW] Template WABA trocado: nova_conversa → novo_atendimento (pt_BR, APROVADO, MARKETING)\n" +
            "• [NEW] Botao QUICK_REPLY 'Continuar Conversa' exibido ao cliente no WhatsApp\n" +
            "• [UI] Interface atualizada: exibe template novo_atendimento / pt_BR / APROVADO\n" +
            "• [UI] Interface exibe botao: Continuar Conversa (QUICK_REPLY)\n" +
            "• [SAFE] Nao envia automaticamente — somente ao clicar no botao WhatsApp\n" +
            "• [SAFE] Anti-spam de 5 minutos mantido\n" +
            "• [SAFE] Normalizacao de numero mantida\n" +
            "• [SAFE] Nao altera SIP, ramal, Waven API, CDR, AMI, contatos ou favoritos\n" +
            "• [LOG] WABA_TEMPLATE_SEND_START | WABA_TEMPLATE_SEND_SUCCESS | WABA_TEMPLATE_SEND_FAIL\n" +
            "\n" +
            "v1.4.4 — WhatsApp WABA template oficial + anti-spam (08/06/2026)\n" +
            "• [NEW] Botao WhatsApp agora envia template nova_conversa via API oficial WABA\n" +
            "• [NEW] Migracao automatica WABA v1.4.4: atualiza URL e token para todos os ramais\n" +
            "• [NEW] Anti-spam: bloqueia reenvio do mesmo numero em menos de 5 minutos\n" +
            "• [NEW] Feedback visual: loading, sucesso e erros amigaveis por codigo HTTP\n" +
            "• [NEW] Tratamento de erros: 401, 404, 429, 500, timeout\n" +
            "• [SAFE] Nao envia automaticamente — somente ao clicar no botao WhatsApp\n" +
            "• [SAFE] Nao depende mais de QR Code para iniciar conversa\n" +
            "• [SAFE] Token nunca aparece completo nos logs (mascarado: 15fdce************12e9)\n" +
            "• [SAFE] Migracao nao altera SIP, ramal, senha, Waven API, audio, contatos, historico\n" +
            "• [LOG] WABA_TEMPLATE_SEND_START | WABA_TEMPLATE_SEND_SUCCESS | WABA_TEMPLATE_SEND_FAIL\n" +
            "• [LOG] WABA_TEMPLATE_BLOCKED | WABA_INVALID_PHONE\n" +
            "• [LOG] AUTO_MIGRATION_WABA_144_APPLIED | AUTO_MIGRATION_WABA_144_ALREADY_APPLIED\n" +
            "\n" +
            "v1.4.3 — Anti-ressurreicao de contatos + Edicao Google + Tombstone (02/06/2026)\n" +
            "• [FIX] Edicao de contato compartilhado voltava nome antigo apos sync — bug timezone UTC/Local no AtualizadoEm\n" +
            "• [FIX] Exclusao de contato ressurregia apos sync — tombstone local previne recriacao pela API\n" +
            "• [FIX] Contato Google editado voltava nome antigo — conversao correta Google→SharedCompany via Waven API\n" +
            "• [FIX] Contato Google excluido reaparecia na sync — tombstone por GoogleContactId e NumeroNormalizado\n" +
            "• [FIX] Google sem GoogleContactId (contatos antigos) nao recebia tombstone — corrigido\n" +
            "• [FIX] Botoes Editar/Excluir de contatos Google sem GoogleContactId mostravam dialog errado\n" +
            "• [FIX] Modal Contato Google: botao cortava texto — largura 420→560px, botoes com tamanhos fixos\n" +
            "• [NEW] ContactTombstoneService: suppression list em contact-tombstones.json\n" +
            "• [NEW] Tombstone por WavenApiId, GoogleContactId e NumeroNormalizado\n" +
            "• [NEW] AplicarContatos: verifica fila offline antes de recriar contato excluido (anti-ressurreicao)\n" +
            "• [NEW] AplicarContatos: nao sobrescreve edicao local se UPDATE pendente na fila offline\n" +
            "• [NEW] Edicao de contato Google com API ativa: cria na Waven API + vincula WavenApiId\n" +
            "• [NEW] SincronizarContatosGoogle: verifica tombstone antes de reimportar\n" +
            "• [UI] Texto do botao: 'Salvar na empresa' (era 'copia local') — deixa claro que e compartilhado\n" +
            "• [UI] Banner verde no modal indicando que sera compartilhado via Waven API\n" +
            "• [LOG] CONTACT_UPDATE_API_START | CONTACT_UPDATE_API_OK | CONTACT_UPDATE_API_FAIL\n" +
            "• [LOG] CONTACT_DELETE_API_START | CONTACT_DELETE_API_OK | CONTACT_DELETE_API_FAIL\n" +
            "• [LOG] GOOGLE_SYNC_SUPPRESSED_BY_TOMBSTONE | GOOGLE_CONTACT_CONVERTED_TO_SHARED\n" +
            "• [LOG] GOOGLE_CONTACT_NOT_OVERWRITING_SHARED | CONTACT_TOMBSTONE_CREATED\n" +
            "• [LOG] API_SYNC_SUPPRESSED_BY_TOMBSTONE | API_CONTACT_UPDATE_SKIPPED_PENDING\n" +
            "\n" +
            "v1.4.2 — Configuracao automatica para todos os ramais (02/06/2026)\n" +
            "• [NEW] Migracao automatica v1.4.2: configura Waven API e WhatsApp API silenciosamente no primeiro start\n" +
            "• [NEW] Waven API ativada automaticamente em todos os ramais apos atualizacao\n" +
            "• [NEW] CDR e AMI habilitados automaticamente para funcionar via API\n" +
            "• [NEW] WhatsApp API atualizado para novo endpoint wavenchat.com.br automaticamente\n" +
            "• [SAFE] Migracao nao sobrescreve usuario SIP, ramal, senha, Google token, audio, favoritos\n" +
            "• [SAFE] Tokens mascarados nos logs — nunca expostos\n" +
            "• [SAFE] Migracao roda apenas uma vez por maquina (flag MigracaoAplicada142)\n" +
            "• [LOG] AUTO_MIGRATION_142_APPLIED | AUTO_MIGRATION_142_ALREADY_APPLIED\n" +
            "\n" +
            "v1.4.1 — CDR e AMI via Waven API (02/06/2026)\n" +
            "• [NEW] Fase 2: CDR via API — historico e gravacoes sem conexao direta ao MySQL\n" +
            "• [NEW] Fase 3: AMI via API — ramais do Issabel sem conexao direta na porta 5038\n" +
            "• [NEW] Quando UsarWavenApi=true: CDR passa por https://api.almeidagas.com/api/cdr/calls\n" +
            "• [NEW] Quando UsarWavenApi=true: AMI passa por https://api.almeidagas.com/api/ami/extensions\n" +
            "• [NEW] API: GET /api/cdr/calls — consulta MySQL local no servidor\n" +
            "• [NEW] API: GET /api/cdr/test — testa conexao MySQL\n" +
            "• [NEW] API: GET /api/cdr/recordings — serve arquivo de gravacao do servidor\n" +
            "• [NEW] API: GET /api/ami/extensions — consulta AMI local no servidor\n" +
            "• [NEW] API: GET /api/ami/status — verifica acessibilidade do AMI\n" +
            "• [NEW] API: POST /api/ami/test — testa login AMI\n" +
            "• [FIX] Sem mais Connect Timeout 3306/5038 no cliente quando UsarWavenApi=true\n" +
            "• [FIX] Credenciais MySQL/AMI ficam APENAS no servidor (appsettings.Production.json)\n" +
            "• [LOG] CLIENT_CDR_USING_API | API_CDR_QUERY_START | API_CDR_QUERY_OK | API_CDR_QUERY_ERROR\n" +
            "• [LOG] CLIENT_AMI_USING_API | API_AMI_CONNECT_START | API_AMI_CONNECT_OK | API_AMI_CONNECT_ERROR\n" +
            "\n" +
            "v1.4.0 — Waven API + Autostart + Bandeja (01/06/2026)\n" +
            "• [NEW] Waven API v1: sincronizacao de contatos compartilhados entre ramais\n" +
            "• [NEW] Favoritos por usuario (ramal) e favoritos globais da empresa\n" +
            "• [NEW] Sincronizacao incremental (since=lastSyncUtc) — apenas mudancas desde ultimo sync\n" +
            "• [NEW] Soft delete com tombstone — exclusao propagada para todos os ramais\n" +
            "• [NEW] Reativacao automatica de contato excluido ao criar com mesmo numero\n" +
            "• [NEW] Offline queue: operacoes CREATE/UPDATE/DELETE/FAVORITE_ADD/FAVORITE_REMOVE enfileiradas offline\n" +
            "• [NEW] Migracao automatica dos 7 favoritos atuais como favoritos globais na primeira ativacao\n" +
            "• [FIX] Offline queue: ops CREATE e UPDATE agora sao executadas ao reconectar (estavam sendo descartadas)\n" +
            "• [FIX] Bandeja: icone carregado com fallback robusto — System.Drawing.SystemIcons.Application se exe nao tiver icone\n" +
            "• [FIX] Bandeja: recuperacao automatica do icone quando Explorer.exe e reiniciado (WM_TASKBARCREATED)\n" +
            "• [FIX] Autostart: caminho registrado no registro usa Environment.ProcessPath (mais confiavel que MainModule.FileName)\n" +
            "• [FIX] Autostart: flag /autostart adicionada ao comando de inicializacao automatica\n" +
            "• [FIX] Autostart: registro SIP atrasado 8s quando iniciado via autostart — aguarda rede estar disponivel\n" +
            "• [FIX] WavenApiSyncService: CancellationToken previne callback em andamento apos Dispose\n" +
            "• [LOG] STARTUP_MODE=AUTOSTART/MANUAL | AUTOSTART_REGISTERED | AUTOSTART_UNREGISTERED\n" +
            "• [LOG] TRAY_ICON_CREATED | TRAY_ICON_RECOVERED | TRAY_ICON_RECREATED | TRAY_ICON_INIT_FAILED\n" +
            "• [LOG] AUTOSTART_SIP_DELAY | AUTOSTART_REGISTER_DELAYED\n" +
            "• [LOG] API_CONTACT_SYNC_START/DONE | API_CONTACT_CREATED/UPDATED/DELETED\n" +
            "• [LOG] API_CONTACT_FAVORITE_MIGRATED | API_CONTACT_OFFLINE_QUEUE_SAVED/SENT\n" +
            "\n" +
            "v1.3.2 — Hold + CDR canal correto (29/05/2026)\n" +
            "• [FIX] CDR: chamadas WhatsApp TIM/Vivo/Operadora nao mais classificadas como Operadora\n" +
            "• [FIX] DedurzirTronco() identifica canal via DID no campo channel do Asterisk (SIP/PJSIP)\n" +
            "• [FIX] Ex: PJSIP/WAVOIP-556684263277-... agora detectado como WhatsApp TIM\n" +
            "• [LOG] CDR_CHANNEL_IDENTIFIED | channel => canal identificado\n" +
            "\n" +
            "v1.3.2 — Hold completo: silencia mic + limpa buffer ao pausar/retomar (ANTERIOR)\n" +
            "• [FIX] Ao pausar (Segurar): mic silenciado via proxy — audio nao acumula no WaveIn\n" +
            "• [FIX] Ao pausar (Segurar): WaveOut pausado — operador nao ouve cliente\n" +
            "• [FIX] Ao pausar e retomar: BufferedWaveProvider.ClearBuffer() chamado via reflexao\n" +
            "• [FIX] Audio residual/eco acumulado no buffer NAudio descartado ao retomar Hold\n" +
            "• [FIX] Ao retomar: estado do mute restaurado ao que era antes da pausa\n" +
            "• [LOG] CALL_HOLD_MIC_MUTED / CALL_HOLD_MIC_RESTORED\n" +
            "• [LOG] CALL_HOLD_WAVEOUT_PAUSED / CALL_HOLD_WAVEOUT_RESUMED\n" +
            "• [LOG] CALL_HOLD_BUFFER_CLEARED\n" +
            "\n" +
            "v1.3.1 — Mute: silence G.711 correto (29/05/2026)\n" +
            "• [FIX] Silence injection usa byte correto por codec (0xFF PCMU, 0xD5 PCMA)\n" +
            "• [FIX] 0x00 (all-zeros) em G.711 decodifica como ruido alto — removido\n" +
            "• [FIX] Asterisk nao mais interpreta pacotes de mute como sinal invalido\n" +
            "• [FIX] SetAudioSourceFormat interceptado para detectar codec negociado\n" +
            "• [FIX] Cliente ouve silencio real durante mute, sem musica ou ruido\n" +
            "\n" +
            "v1.3.0 — Mute TX-Only correto via silence injection (29/05/2026)\n" +
            "• [FIX] Mute aplica SOMENTE no pipeline TX (microfone/envio) — cliente nao me escuta\n" +
            "• [FIX] Audio recebido do cliente (RX/WaveOut) continua 100% ativo durante mute\n" +
            "• [FIX] RTP TX continua fluindo com silence — Asterisk nao descarta sessao apos timeout\n" +
            "• [FIX] Mute de qualquer duracao (inclusive >3 min) nao derruba a chamada\n" +
            "• [FIX] Descligar chamada mutada reseta mute — proxima chamada comeca desmutada\n" +
            "• [NEW] MuteableAudioSource: proxy IAudioSource que injeta silence encodado no TX\n" +
            "• [NEW] AudioSource=proxy (TX), AudioSink=endpoint direto (RX) — separacao completa\n" +
            "• [LOG] CALL_MUTE_ENABLED_TX_ONLY — mute ativado somente no TX\n" +
            "• [LOG] CALL_MUTE_DISABLED_TX_RESTORED — microfone restaurado\n" +
            "• [LOG] CALL_RX_AUDIO_CONTINUES_DURING_MUTE — confirmacao que WaveOut nao foi afetado\n" +
            "• [LOG] CALL_MUTE_DID_NOT_AFFECT_PLAYBACK — pipeline RX intacto\n" +
            "• [LOG] CALL_MUTE_STATE_RESET_ON_END — mute resetado ao encerrar chamada\n" +
            "• [LOG] AUDIO_MUTE_PROXY_CREATED — proxy criado na sessao de midia\n" +
            "\n" +
            "v1.2.14 — Historico CDR + Gravacoes + Contatos da Empresa (28/05/2026)\n" +
            "• [FIX] CDR: chamada sainte corretamente associada ao numero real discado\n" +
            "  — CDR do tronco (src=DID) agora extrai destino real do nome do arquivo de gravacao\n" +
            "  — Numero e tipo (Realizada) corrigidos automaticamente pelo prefixo de rota no filename\n" +
            "• [FIX] CDR: canal/tronco exibido corretamente nas saintes (Operadora/WhatsApp TIM/Vivo)\n" +
            "  — Prefixo 1/2/3 do filename de gravacao determina a origem da saida\n" +
            "• [FIX] CDR: duplicatas de chamadas recebidas removidas automaticamente\n" +
            "  — MesclarCdr normaliza 55+numero antes de comparar entrada local com CDR\n" +
            "  — ReprocessarHistorico limpa duplicatas legadas no historico.json existente\n" +
            "• [FIX] CDR: entrada local SIP removida corretamente ao sincronizar CDR (normalize +55)\n" +
            "• [UI] Botao X para limpar pesquisa nos paineis Contatos e Historico\n" +
            "  — Visivel apenas quando ha texto; foco retorna ao campo apos limpar\n" +
            "• [NEW] Sincronizacao automatica de contatos da empresa entre todos os ramais (GitHub raw, 5 min)\n" +
            "• [NEW] Favoritos sempre no topo da lista de contatos, restante em ordem alfabetica\n" +
            "• [NEW] Estrela colorida: cinza=nao favorito, amarelo=favorito (com tooltip)\n" +
            "• [NEW] Deduplicacao automatica de contatos por numero normalizado (local > Google)\n" +
            "• [NEW] Contato duplicado no cadastro: atualiza existente em vez de bloquear\n" +
            "• [NEW] company-contacts.json: 7 contatos/favoritos distribuidos para todos os ramais\n" +
            "• [NEW] company-config.json: config WhatsApp + CDR centralizada e auto-sincronizada\n" +
            "• [FIX] Mute sem queda de chamada: MutarMicrofone() nao para RTP TX\n" +
            "• [FIX] Watchdog RTP: keepalive a cada 20s durante mute (previne timeout Asterisk)\n" +
            "• [FIX] Transferencia: flag _transferenciaEmAndamento impede destruicao prematura de sessao\n" +
            "• [FIX] PhoneNumberNormalizer: remove 55 de numeros 12-13 digitos + casos rota+55\n" +
            "• [LOG] CDR_RECORDING_CORRECTS_NUMBER / REPROCESS_DUPLICATE_REMOVED\n" +
            "• [LOG] CONTACTS_SYNC_START / CONTACTS_SYNC_DONE / CONTACT_CREATED / CONTACT_UPDATED\n" +
            "• [LOG] CONTACT_DELETED / FAVORITE_SYNCED / CONTACTS_DEDUP_UI\n" +
            "• [LOG] COMPANY_CONTACTS_IMPORTED_ON_UPDATE / COMPANY_FAVORITES_IMPORTED_ON_UPDATE\n" +
            "• [LOG] COMPANY_CONTACTS_EXPORT_START / COMPANY_CONTACTS_EXPORT_DONE\n" +
            "• [LOG] CALL_START_REQUEST / CALL_CONNECTED / CALL_MUTE_ENABLED / CALL_MUTE_DISABLED\n" +
            "• [LOG] AUDIO_DEVICE_DETECTED / AUDIO_DEVICE_SWITCH / AUDIO_DEVICE_RECOVERY\n" +
            "\n" +
            "v1.2.13 — Audio inteligente (26/05/2026)\n" +
            "• [NEW] Separacao entre audio da conversa e toque de chamada\n" +
            "• [NEW] Toque pode sair no alto-falante enquanto chamada usa headset\n" +
            "• [NEW] Volume do toque configuravel (slider 0-100)\n" +
            "• [NEW] Auto-deteccao de headset Intelbras USB na abertura de Configuracoes\n" +
            "• [NEW] Campos: Microfone da ligacao, Audio da ligacao, Saida do toque\n" +
            "• [NEW] Botao 'Testar toque' em Configuracoes com volume real\n" +
            "• [FIX] Compatibilidade com headset Intelbras USB\n" +
            "• [FIX] Toque NUNCA sai no headset se houver alto-falante disponivel\n" +
            "• [LOG] AUDIO_DEVICE_DETECTED / AUDIO_HEADSET_INTELBRAS_DETECTED\n" +
            "• [LOG] AUDIO_AUTO_INPUT_SELECTED / AUDIO_AUTO_CALL_OUTPUT_SELECTED\n" +
            "• [LOG] AUDIO_AUTO_RING_OUTPUT_SELECTED / AUDIO_RING_VOLUME_SET\n" +
            "• [LOG] AUDIO_LEGACY_CONFIG_MIGRATED / AUDIO_RING_TEST_PLAYED\n" +
            "\n" +
            "v1.2.12 — Hotfix Configuracoes (25/05/2026)\n" +
            "• [FIX] Janela Configuracoes nao abria apos atualizacao v1.2.11\n" +
            "• [FIX] StaticResource SectionCardStyle/SectionTitleStyle inexistentes removidos\n" +
            "• [FIX] Secao Logs e Diagnostico reescrita com estilos inline corretos\n" +
            "\n" +
            "v1.2.11 — Logs e Diagnostico (25/05/2026)\n" +
            "• [NEW] Seção 'Logs e Diagnóstico' em Configurações\n" +
            "• [NEW] Checkbox: Ativar logs do sistema (persiste em SipConfig)\n" +
            "• [NEW] Checkbox: Ativar logs detalhados — SIP, AMI, CDR\n" +
            "• [NEW] Indicador visual de status dos logs (verde=ativo, cinza=desativado)\n" +
            "• [NEW] Botão 'Abrir pasta de logs' — abre %LOCALAPPDATA%\\WavenVoIP\\Logs\n" +
            "• [NEW] Botão 'Copiar diagnóstico' — copia resumo + últimas 30 linhas de cada canal\n" +
            "• [NEW] Botão 'Limpar logs' — remove todos os arquivos .log (com confirmação)\n" +
            "• [NEW] Botão 'Exportar diagnóstico ZIP' — gera ZIP com logs + resumo de ambiente\n" +
            "• [NEW] LogHelper.ConfigurarDeSettings(SipConfig): aplica flags ao iniciar\n" +
            "• [NEW] LogHelper.IsEnabled / IsDetailedEnabled — ERRORs sempre gravados\n" +
            "• [NEW] LogHelper.Google() e LogHelper.WhatsApp() — canais dedicados\n" +
            "• [FIX] Flags de log lidas no startup via App.xaml.cs\n" +
            "\n" +
            "v1.2.10 — Migração GitHub Releases (25/05/2026)\n" +
            "• [INFRA] Distribuição migrada de Hostinger para GitHub Releases\n" +
            "• [FIX] SHA256 do pacote agora idêntico no download (sem recompressão do servidor)\n" +
            "• [FIX] Updater não precisa mais de headers anti-WAF/ModSecurity\n" +
            "• [INFRA] version.json servido via GitHub raw (sempre sincronia com o repo)\n" +
            "\n" +
            "v1.2.9 — Reconexão Automática Silenciosa (25/05/2026)\n" +
            "• [NEW] IntegrationFailureClassifier: distingue falha temporária de falha de autenticação\n" +
            "• [NEW] IntegrationAutoReconnectService: backoff 5s→15s→30s→1m→3m→5m por integração\n" +
            "• [NEW] Status Reconectando (🟡 amarelo) — sem modal, sem ação necessária\n" +
            "• [FIX] Queda de internet: status Reconectando, tentativa automática, ZERO modal\n" +
            "• [FIX] Credencial inválida: status Ação necessária + modal + botão Reconectar\n" +
            "• [FIX] AMI socket/timeout: reconexão automática sem perturbar usuário\n" +
            "• [FIX] CDR MySQL offline: reconexão automática; access denied → modal\n" +
            "• [FIX] Google sem internet: reconexão automática; token revogado → modal\n" +
            "• [LOG] INTEGRATION_TEMPORARY_FAILURE / INTEGRATION_AUTO_RECONNECT_SCHEDULED\n" +
            "• [LOG] INTEGRATION_AUTO_RECONNECT_ATTEMPT / INTEGRATION_AUTO_RECONNECT_SUCCESS\n" +
            "• [LOG] INTEGRATION_ACTION_REQUIRED / INTEGRATION_MODAL_SUPPRESSED_TEMPORARY\n" +
            "• [LOG] INTEGRATION_MODAL_SHOWN_AUTH_FAILURE\n" +
            "\n" +
            "v1.2.8 — Painel de Logs em Tempo Real (25/05/2026)\n" +
            "• [NEW] Log panel em tempo real via LogHelper.LogWritten event\n" +
            "• [NEW] Filtros rápidos: Todos / Erros / Updater / Google / AMI / WhatsApp / SIP\n" +
            "• [NEW] Cores: ERROR=vermelho, WARN=amarelo, SUCCESS=verde, INFO=cinza\n" +
            "• [NEW] Checkbox Rolagem automática\n" +
            "• [NEW] Botão Limpar logs (visual only — arquivo físico intacto)\n" +
            "• [NEW] Botão Copiar logs (copia conteúdo visível para clipboard)\n" +
            "• [NEW] Botão Abrir pasta de logs (%LOCALAPPDATA%\\WavenVoIP\\Logs)\n" +
            "• [NEW] Carga inicial dos últimos 400 linhas por canal na abertura\n" +
            "• [FIX] Painel lê logs de %LOCALAPPDATA%\\WavenVoIP\\Logs\\ (caminho correto)\n" +
            "• [SAFE] Append assíncrono via Dispatcher.BeginInvoke — UI nunca trava\n" +
            "• [SAFE] Limite de 2000 entradas na UI; arquivo físico completo\n" +
            "\n" +
            "v1.2.7 — Sistema Global de Reconexão Manual (24/05/2026)\n" +
            "• [NEW] IntegrationStatusService: status Conectado/Desconectado/Erro para todas as integrações\n" +
            "• [NEW] IntegrationDisconnectedOverlay: modal uma vez por sessão por integração\n" +
            "• [NEW] Anti-loop global: bloqueia reconexão por 10 minutos após tentativa\n" +
            "• [FIX] Google OAuth: NUNCA abre navegador automaticamente — só via botão Conectar Google\n" +
            "• [FIX] AMI: retry silencioso, sem popup infinito; modal apenas na primeira falha por sessão\n" +
            "• [FIX] CDR: marca 'CDR indisponível', app continua funcionando; modal uma vez por sessão\n" +
            "• [UI] Configurações > Status das Integrações: dots coloridos + botões Conectar/Desconectar/Reconectar\n" +
            "• [LOG] INTEGRATION_MODAL_SHOWN / INTEGRATION_MODAL_DISMISSED / INTEGRATION_LOOP_BLOCKED\n" +
            "• [LOG] INTEGRATION_RECONNECT_ATTEMPT / GOOGLE_DISCONNECTED / GOOGLE_TOKEN_MISSING\n" +
            "\n" +
            "v1.2.6 — Download 403 Fix V2 (24/05/2026)\n" +
            "• [FIX] DOWNLOAD_CODE_VERSION=2026-05-24-FIX403-V2 marker confirma binario novo\n" +
            "• [FIX] Headers completos: User-Agent Chrome, Accept, Accept-Language, Referer, Cache-Control\n" +
            "• [FIX] HttpClientHandler: AutomaticDecompression gzip/deflate/br + AllowAutoRedirect\n" +
            "• [NEW] DOWNLOAD_PRECHECK_START: HEAD pre-check loga Server, CF-Ray, X-ModSecurity, Content-Length\n" +
            "• [NEW] DOWNLOAD_FALLBACK_START: fallback WebClient com mesmos headers quando HttpClient recebe 403\n" +
            "• [LOG] DOWNLOAD_ERROR_BODY: primeiros 500 chars do HTML de bloqueio do WAF\n" +
            "• [LOG] WEBCLIENT_ERROR: exception completa do fallback WebClient\n" +
            "\n" +
            "v1.2.5 — Correcao Download Hostinger 403 (24/05/2026)\n" +
            "• [FIX] Download do pacote bloqueado com HTTP 403 pela Hostinger (ModSecurity/WAF)\n" +
            "• [FIX] HttpClient agora envia User-Agent de navegador e cabecalhos Accept identicos ao Chrome\n" +
            "• [FIX] TLS 1.2/1.3 forcado explicitamente via ServicePointManager\n" +
            "• [FIX] Descompressao automatica ativada (gzip/deflate/br)\n" +
            "• [NEW] Fallback para WebClient quando HttpClient recebe 403\n" +
            "• [LOG] HEAD pre-check antes do download: status, Content-Length, Server, CF-Ray, X-ModSecurity\n" +
            "• [LOG] Corpo da resposta 403 salvo no log (ate 500 chars) para diagnostico\n" +
            "\n" +
            "v1.2.4 — Correcao WavenUpdater (24/05/2026)\n" +
            "• [FIX] WavenUpdater nao fecha mais imediatamente apos ser iniciado\n" +
            "• [FIX] Todos os 4 arquivos do .NET host copiados para pasta temp (exe+dll+deps+runtimeconfig)\n" +
            "• [FIX] UseShellExecute=true restaurado — necessario para inicializacao WPF/window station\n" +
            "• [LOG] APP_STARTUP / APP_RUNTIME / APP_WORKDIR / APP_BASE_DIR no update.log\n" +
            "• [LOG] LAUNCH_CMD / LAUNCH_WORKDIR com caminho e argumentos exatos do processo\n" +
            "• [SAFE] AppDomain.UnhandledException + DispatcherUnhandledException no WavenUpdater\n" +
            "\n" +
            "v1.2.3 — Protecao Instalacao Nova (24/05/2026)\n" +
            "• [FIX] Instalacao limpa nao abre mais com usuario anterior (ex: Pabliny)\n" +
            "• [FIX] fresh_install.flag criado pelo instalador reseta identidade do usuario\n" +
            "• [FIX] Configuracoes avancadas (AMI/CDR/WhatsApp) sao preservadas na reinstalacao\n" +
            "• [UI] Instalador: checkbox 'Preservar configuracoes de usuario existentes'\n" +
            "• [LOG] INSTALL_MODE=FRESH/UPGRADE / FRESH_INSTALL_FLAG_FOUND / USER_IDENTITY_RESET\n" +
            "\n" +
            "v1.2.2 — Protecao Anti-Loop de Update (24/05/2026)\n" +
            "• [FIX] Update nao fecha mais o WavenVoIP se o WavenUpdater falhar ao iniciar\n" +
            "• [FIX] Flag update_failed.flag criada automaticamente em caso de falha — pausa updates automaticos\n" +
            "• [FIX] Anti-loop: 2 tentativas em menos de 10 min bloqueiam update por 24h\n" +
            "• [UI] Configuracoes > Atualizacoes: banner de aviso + botao 'Limpar falha'\n" +
            "• [UI] WavenUpdater: botoes 'Abrir WavenVoIP' e 'Copiar log' ao falhar (nao fecha sozinho)\n" +
            "• [LOG] UPDATE_LOOP_GUARD / UPDATER_START_OK / UPDATER_START_FAILED / APP_CLOSE_CANCELLED\n" +
            "\n" +
            "v1.2.1 — Correncao URL Pacote .zip (24/05/2026)\n" +
            "• [FIX] Pacote renomeado de WavenVoIP.pkg para WavenVoIP.zip\n" +
            "• [LOG] CHECK_URL / CHECK_JSON / PACOTE / DOWNLOAD_URL / DOWNLOAD_HTTP adicionados\n" +
            "• [UI] WavenUpdater: botao 'Copiar log' no rodape\n" +
            "\n" +
            "v1.2.0 — Auto-Update + Icone Oficial (24/05/2026)\n" +
            "• [NEW] Sistema de atualizacao automatica silenciosa sem UAC\n" +
            "• [NEW] Icone oficial: arte completa W+headset em PNG transparente\n" +
            "• [NEW] Painel de Atualizacoes em Configuracoes com progresso e controle manual\n" +
            "\n" +
            "v1.1.2 — WhatsApp Incoming Call Delay Fix (19/05/2026)\n" +
            "• [FIX] OnIncomingCall: DetectarOrigemEntrada (reflexão + I/O disco) movido para Task.Run após evento\n" +
            "• [FIX] Removido EnviarRespostaTentandoETocando duplicado em OnIncomingCall (já enviado no transport)\n" +
            "• [FIX] CanalIdentificacaoService: SipConfig cacheada 30 s (evita leitura de arquivo por INVITE)\n" +
            "• [FIX] Dispatcher.Invoke → BeginInvoke no popup de chamada recebida (SIP thread não bloqueia)\n" +
            "• [LOG] WHATSAPP_INCOMING_DETECTED / WHATSAPP_CALL_EVENT_RECEIVED / EXTENSION_RING_STARTED\n" +
            "• [LOG] WHATSAPP_FORWARD_TOTAL_DELAY_MS / WHATSAPP_DUPLICATE_EVENT_IGNORED / SIP_INVITE_TO_ISSABEL_SENT\n" +
            "\n" +
            "v1.1.1 — Contact Migration Safe Rules (19/05/2026)\n" +
            "• [FIX] MigrarContatosAntigos: números 14+ dígitos marcados como CONTACT_NUMBER_SUSPICIOUS, não alterados\n" +
            "• [FIX] Guard de execução única por inicialização (_migracaoJaExecutada)\n" +
            "• [FIX] Timeout de 10 segundos — migração nunca trava a UI\n" +
            "• [LOG] Relatório: migrados / deduplicados / suspeitos / total\n" +
            "\n" +
            "v1.1.0 — UI Modernization (20/05/2026)\n" +
            "• [UI] CallWindow: avatar com iniciais, dot de status com animação pulse, indicador de gravação, tooltips\n" +
            "• [UI] TransferControlWindow: layout profissional, avatar, timer, cards dual-call, botões coloridos\n" +
            "• [UI] BlindTransferWindow: nova tela de confirmação com card de contato e aviso antes de transferir\n" +
            "• [UI] ConferenceControlWindow: participantes com avatar, dot de status colorido por estado\n" +
            "• [FIX] Transferência cega agora exibe confirmação com nome/número antes de executar\n" +
            "• [FIX] TransferControlWindow recebe info da chamada original para exibir no card dual-call\n" +
            "\n" +
            "v1.0.2 — Estabilização final de normalização (19/05/2026)\n" +
            "• [FIX] Prefixo 55 removido de todas as telas: histórico, popup chamada, tela em chamada, notificações, transferência, conferência, WhatsApp, contatos\n" +
            "• [FIX] Nome do contato exibido no popup de chamada entrante: 'Nome (número)' quando encontrado\n" +
            "• [FIX] Mesmo formato 'Nome (número)' em MissedCallToast, MissedCallPopup, CallWindow e ConferenceControlWindow\n" +
            "• [FIX] Labels SIP técnicos (SIP, PJSIP, DAHDI) substituídos por 'Chamada do sistema'\n" +
            "• [FIX] HistoricoLigacaoItem.NumeroLimpoVisual agora remove prefixo 55 além do prefixo de rota\n" +
            "• [FIX] ContatoStorageService: busca por variantes (com/sem 9º dígito, com/sem 55) — mais matches\n" +
            "• [FIX] CDR timer padrão 5 s com campo HistoricoSyncIntervalSeconds e migração automática\n" +
            "• [LOG] CONTACT_MATCH_SUCCESS / CONTACT_MATCH_FAIL / PHONE_NORMALIZED em ui_flow_debug.log\n" +
            "• [FIX] Modal SalvarContatoDialog: altura aumentada (460→560) para não cortar botões com banner Google\n" +
            "• [FIX] ContactsWindow: deduplicação por número normalizado (local > AMI > Google)\n" +
            "\n" +
            "v1.0.1 — Hotfix crítico CDR (19/05/2026)\n" +
            "• [FIX] Crash 'An item with the same key has already been added' na sincronização CDR\n" +
            "  — linkedid/uniqueid duplicado do Asterisk (filas/URA/transferências) não aborta mais a sync\n" +
            "  — TryAdd substitui ToDictionary; duplicatas logadas como CDR_DUPLICATE_KEY_DETECTED\n" +
            "\n" +
            "v1.0.0 — Versão base definitiva (18/05/2026)\n" +
            "• CDR com deduplicação de fila e ramal real\n" +
            "• Validação rigorosa de ramais (2-5 dígitos)\n" +
            "• Detecção de caixa postal e URA/IVR\n" +
            "• Gravações com fallback via diretório HTTP + HEAD validation\n" +
            "• Sync de CDR em 5 s / 15 s / 30 s pós-chamada\n" +
            "• Status mostrando nome do operador (ramal)";
    }
}
