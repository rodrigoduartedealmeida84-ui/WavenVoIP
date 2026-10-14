using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using WavenVoIP.Services;

namespace WavenVoIP.Models
{
    public enum TipoHistoricoLigacao
    {
        Realizada = 0,
        Recebida = 1,
        Perdida = 2,
        NaoAtendidaNesseRamal = 3,
        CaixaPostal = 4,
        // v2.3.5 — chamadas REALIZADAS (operador ligou) que não completaram nunca usam
        // Perdida (exclusivo de chamadas recebidas — ver ClassificarChamada). Recusada = o
        // cliente rejeitou/estava ocupado (CDR disposition BUSY); NaoAtendida = tocou até
        // acabar sem resposta (NO ANSWER/FAILED/CONGESTION).
        Recusada = 5,
        NaoAtendida = 6,
        // v2.3.6 — chamada REALIZADA que o próprio OPERADOR encerrou/desistiu pelo Waven antes do
        // cliente atender (ex.: discou errado e percebeu ainda tocando). Nunca deve ser confundida
        // com Recusada (ação do cliente) nem NaoAtendida (timeout do lado do cliente) — a fonte de
        // verdade é local (SipService.LastOutboundWasCancelledLocally), nunca inferida pelo CDR.
        Cancelada = 7
    }

    public class HistoricoLigacaoItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Numero { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public TipoHistoricoLigacao Tipo { get; set; }
        public DateTime DataHora { get; set; } = DateTime.Now;
        public string Duracao { get; set; } = "00:00";

        // CDR fields (empty/null for locally-tracked calls)
        public string UniqueId { get; set; } = string.Empty;
        public string LinkedId { get; set; } = string.Empty;
        public string RamalOrigem { get; set; } = string.Empty;
        public string RamalDestino { get; set; } = string.Empty;
        public string RamalAtendeu { get; set; } = string.Empty;
        public string GravacaoArquivo { get; set; } = string.Empty;
        public string GravacaoUrl { get; set; } = string.Empty;
        public bool FonteCdr { get; set; } = false;

        // v2.3.6 — canal EXTERNO real de entrada (Operadora/0800/WhatsApp TIM/WhatsApp Vivo),
        // independente do fluxo interno (URA/fila) percorrido depois. Diferente de OrigemSaida,
        // que pode ficar "Queue" quando a ligação passa por fila/URA antes de chegar num agente
        // — nesse caso OrigemSaidaVisual ainda mostra "URA"/"Abandonada na fila" (comportamento
        // preservado), mas CanalEntrada guarda o canal real para uso pelo botão "Retornar chamada".
        // Vazio = não foi possível identificar com confiança (registros antigos, ou nenhum DID/
        // gravação reconhecido) — nesse caso o retorno usa o seletor normal, sem perguntar nada.
        public string CanalEntrada { get; set; } = string.Empty;

        // v2.3.6 — marcador PERSISTENTE (sobrevive a sync/reprocessar/restart, diferente de
        // SipService.LastOutboundWasCancelledLocally, que só existe em memória durante a tentativa).
        // true exclusivamente quando o PRÓPRIO OPERADOR clicou Desligar no Waven enquanto a chamada
        // de saída ainda não tinha sido atendida (nunca setado se a chamada já estava conectada — ver
        // IniciarLigacaoAsync). Serve de prova irrefutável de que Tipo=Cancelada é um resultado LOCAL
        // confiável — nenhum disposition genérico de CDR chegado depois (mesmo ANSWERED/billsec>0,
        // possível no tronco WAVOIP antes do destino real atender) pode reclassificar esse registro.
        // Campo novo opcional — historico.json antigo sem ele desserializa como false, sem quebrar.
        public bool CanceladaPeloOperador { get; set; } = false;

        // v2.5.0 — chamada RECEBIDA que só o próprio PABX atendeu (URA ou anúncio, ex.: mensagem
        // de fora do horário) e que terminou sem chegar a nenhum ramal. O CDR marca ANSWERED
        // porque o Asterisk atende o canal para tocar o áudio, então o Tipo fica Recebida mas não
        // existe atendente humano a mostrar — antes o Histórico exibia a linha sem ninguém, como
        // se faltasse o nome de quem atendeu. Valores: "URA", "Anuncio", "ForaHorario" ou vazio
        // (ver IssabelCdrService.IdentificarAtendimentoAutomatico). Campo novo opcional —
        // historico.json antigo sem ele desserializa como vazio.
        // O Tipo persistido continua Recebida de propósito: um valor novo no enum quebraria a
        // leitura do historico.json por uma versão anterior (rollback para a 2.4.7).
        public string AtendimentoAutomatico { get; set; } = string.Empty;

        // Recebida que só o PABX atendeu — não é atendimento de operador.
        [JsonIgnore]
        public bool EhAtendimentoAutomatico =>
            (Tipo == TipoHistoricoLigacao.Recebida ||
             // fora do horário que seguiu até a caixa postal 400 (o CDR vira "VoiceMail")
             (Tipo == TipoHistoricoLigacao.CaixaPostal &&
              string.Equals(AtendimentoAutomatico, "ForaHorario", StringComparison.OrdinalIgnoreCase))) &&
            string.IsNullOrWhiteSpace(RamalAtendeu) &&
            !string.IsNullOrWhiteSpace(AtendimentoAutomatico);

        // Chegou fora do horário de atendimento e ouviu a mensagem automática (confirmado pelo
        // contexto/áudio do CDR, nunca pelo relógio).
        [JsonIgnore]
        public bool EhForaDoHorario =>
            EhAtendimentoAutomatico &&
            string.Equals(AtendimentoAutomatico, "ForaHorario", StringComparison.OrdinalIgnoreCase);

        // "Entrada" que é só a ponta de volta de uma ligação feita pelo próprio ramal para uma
        // linha da empresa (ver IssabelCdrService.EhRetornoDaPropriaLigacao): não há cliente.
        // v2.5.0 — por que uma chamada recebida terminou sem atendimento (só para Tipo = Perdida):
        //   "ClienteDesistiu" = o cliente desligou enquanto os ramais ainda tocavam;
        //   "AbandonoFila"    = o cliente desligou enquanto aguardava na fila;
        //   ""                = o toque/a fila se esgotou sem ninguém atender (perdida de fato).
        // Os três geram aviso de retorno; cada um tem seu indicador.
        // Ver IssabelCdrService.MotivoDaPerdida. Campo opcional: históricos antigos ficam "".
        public string MotivoPerdida { get; set; } = string.Empty;

        [JsonIgnore]
        public bool EhDesistenciaRapida =>
            Tipo == TipoHistoricoLigacao.Perdida && string.Equals(MotivoPerdida, "ClienteDesistiu", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool EhAbandonoFila =>
            Tipo == TipoHistoricoLigacao.Perdida && string.Equals(MotivoPerdida, "AbandonoFila", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool EhEventoTecnico =>
            EhAtendimentoAutomatico &&
            string.Equals(AtendimentoAutomatico, "RetornoProprio", StringComparison.OrdinalIgnoreCase);

        // Recebida de fato atendida por um operador (exclui URA/anúncio/fora do horário).
        [JsonIgnore]
        public bool EhRecebidaPorOperador =>
            Tipo == TipoHistoricoLigacao.Recebida && !EhAtendimentoAutomatico;

        // ── Computed display ──────────────────────────────────────────────────────

        public string DataHoraFormatada => DataHora.ToString("dd/MM/yyyy HH:mm");

        [JsonIgnore]
        public string DataHoraRelativa
        {
            get
            {
                var delta = DateTime.Now - DataHora;
                if (delta.TotalMinutes < 1) return "Agora";
                if (delta.TotalMinutes < 60) return $"Há {(int)delta.TotalMinutes} min";
                if (delta.TotalHours < 24 && DataHora.Date == DateTime.Today) return $"Hoje {DataHora:HH:mm}";
                if (DataHora.Date == DateTime.Today.AddDays(-1)) return $"Ontem {DataHora:HH:mm}";
                return DataHora.ToString("dd/MM HH:mm");
            }
        }

        [JsonIgnore]
        public string NomeExibido
        {
            get
            {
                try
                {
                    var numLimpo = LimparPrefixoRota(Numero); // strips route prefix + country code 55
                    if (string.IsNullOrWhiteSpace(numLimpo))
                        return string.IsNullOrWhiteSpace(Nome) ? "Chamada do sistema" : FiltrarLabelSip(Nome);

                    var resolved = ContatoStorageService.ResolverNomePorNumero(numLimpo);
                    if (!string.Equals(resolved, numLimpo, StringComparison.OrdinalIgnoreCase))
                        return resolved;

                    if (!string.IsNullOrWhiteSpace(Nome) &&
                        !string.Equals(Nome, Numero, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Nome, numLimpo, StringComparison.OrdinalIgnoreCase) &&
                        !EhLabelSipTecnico(Nome))
                    {
                        // Normalizar Nome puramente numérico para corrigir números concatenados
                        // vindos do CDR (ex: "6699661750066996617500" → "66996617500").
                        var soDigitos = new string(Nome.Where(char.IsDigit).ToArray());
                        if (soDigitos.Length > 0 &&
                            string.Equals(soDigitos, Nome.Trim(), StringComparison.Ordinal))
                        {
                            var nomeNorm = LimparPrefixoRota(soDigitos);
                            if (!string.Equals(nomeNorm, numLimpo, StringComparison.OrdinalIgnoreCase))
                                return nomeNorm;
                            // Nome era o número concatenado — retornar numLimpo abaixo
                        }
                        else
                        {
                            return Nome;
                        }
                    }

                    return numLimpo;
                }
                catch { return string.IsNullOrWhiteSpace(Nome) ? Numero : Nome; }
            }
        }

        public string OrigemSaida { get; set; } = "";

        public string NumeroLimpoVisual
        {
            get
            {
                var ramal = RamalDaConversaInterna();
                if (!string.IsNullOrWhiteSpace(ramal)) return ramal;
                return LimparPrefixoRota(Numero);
            }
        }

        // Retorna o número limpo apenas quando NomeExibido é diferente (há nome real).
        // Quando não há contato salvo, NomeExibido JÁ é o número — evita duplicidade.
        [JsonIgnore]
        public string NumeroSubtitulo
        {
            get
            {
                var nome = NomeExibido;
                var num  = NumeroLimpoVisual;
                if (string.IsNullOrWhiteSpace(num)) return string.Empty;
                if (string.Equals(nome, num, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                return num;
            }
        }

        [JsonIgnore]
        public Visibility VisibilidadeNumero =>
            string.IsNullOrWhiteSpace(NumeroSubtitulo) ? Visibility.Collapsed : Visibility.Visible;

        public string OrigemSaidaVisual
        {
            get
            {
                // When both ramal fields confirm an internal call, override any stored channel.
                if (EhChamadaRamalInterno()) return "Ramal interno";

                // v2.3.6 — CanalEntrada é o canal externo real (Operadora/0800/WhatsApp TIM/Vivo),
                // resolvido mesmo quando a ligação passou por URA/fila antes de chegar num agente.
                // Tem prioridade sobre o rótulo de fluxo interno ("URA") — a URA é só uma etapa,
                // não o canal por onde a ligação realmente entrou. Exceção: quando o desfecho é
                // abandono na fila (Perdida/NaoAtendidaNesseRamal com OrigemSaida=="Queue"), mantém
                // "Abandonada na fila"/"Desligou antes da fila" — esse rótulo já foi validado e
                // corrige uma atribuição falsa de operador (ver v2.2.1).
                var ehAbandonoNaFila =
                    (Tipo == TipoHistoricoLigacao.Perdida || Tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal) &&
                    string.Equals(OrigemSaida, "Queue", StringComparison.OrdinalIgnoreCase);
                if (!ehAbandonoNaFila && !string.IsNullOrWhiteSpace(CanalEntrada))
                    return CanalEntrada;

                var canal = CanalIdentificacaoService.NormalizarCanal(OrigemSaida, Tipo, Numero);
                if (canal == "Saída não identificada" && EhChamadaRealizadaOuFalhaDeSaida())
                    return DetectarSaidaPeloPrefixo(Numero);
                return canal;
            }
        }

        private bool EhChamadaRamalInterno() =>
            !string.IsNullOrWhiteSpace(RamalOrigem) &&
            !string.IsNullOrWhiteSpace(RamalDestino) &&
            WavenVoIP.DialPlanService.EhRamalInterno(RamalOrigem) &&
            WavenVoIP.DialPlanService.EhRamalInterno(RamalDestino) &&
            !string.Equals(RamalOrigem, RamalDestino, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(OrigemSaida, "Queue", StringComparison.OrdinalIgnoreCase) &&
            OrigemSaida.IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) < 0;

        // Returns the "other party" ramal for internal ramal-to-ramal conversations.
        private string RamalDaConversaInterna()
        {
            if (!EhChamadaRamalInterno()) return string.Empty;
            return EhChamadaRealizadaOuFalhaDeSaida() ? RamalDestino : RamalOrigem;
        }

        // Realizada, Recusada e NaoAtendida são todas chamadas que ESTE ramal originou
        // (RamalOrigem = quem discou) — só o desfecho final muda. Usado para decidir, em vários
        // pontos, se o "lado do operador" é RamalOrigem (saída) ou RamalDestino/RamalAtendeu (entrada).
        private bool EhChamadaRealizadaOuFalhaDeSaida() =>
            Tipo == TipoHistoricoLigacao.Realizada ||
            Tipo == TipoHistoricoLigacao.Recusada ||
            Tipo == TipoHistoricoLigacao.NaoAtendida ||
            Tipo == TipoHistoricoLigacao.Cancelada;

        [JsonIgnore]
        public Brush CorCanal => CanalIdentificacaoService.BrushCanal(OrigemSaidaVisual);

        // "\uE708" = lua (QuietHours) da fonte de ícones do Windows; ver IconeEhSimboloDoSistema.
        public string Icone => EhForaDoHorario ? "\uE708" : EhEventoTecnico ? "⟲" : Tipo switch
        {
            TipoHistoricoLigacao.Realizada => "↗",
            TipoHistoricoLigacao.Recebida => "↙",
            TipoHistoricoLigacao.Perdida => "↘",
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => "↩",
            TipoHistoricoLigacao.CaixaPostal => "✉",
            TipoHistoricoLigacao.Recusada => "⊘",
            TipoHistoricoLigacao.NaoAtendida => "↛",
            TipoHistoricoLigacao.Cancelada => "⦸",
            _ => "•"
        };

        // O ícone é um glifo da "Segoe MDL2 Assets" (a lista troca a fonte só nesse caso).
        [JsonIgnore]
        public bool IconeEhSimboloDoSistema => EhForaDoHorario;

        // v2.5.0 — "Fora do horário" é só rótulo/filtro: o Tipo persistido continua Recebida.
        public string TipoTexto => EhForaDoHorario ? "Fora do horário" : EhEventoTecnico ? "Evento técnico" : Tipo switch
        {
            TipoHistoricoLigacao.Realizada => "Realizada",
            TipoHistoricoLigacao.Recebida => "Recebida",
            TipoHistoricoLigacao.Perdida => EhDesistenciaRapida ? "Cliente desistiu" : EhAbandonoFila ? "Abandono na fila" : "Perdida",
            // Renomeado de "Não atendida" (v2.3.5): esse status é para chamada RECEBIDA que
            // tocou neste ramal mas foi atendida por outro — texto antigo colidia com o novo
            // TipoHistoricoLigacao.NaoAtendida (chamada REALIZADA sem resposta).
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => "Atendida em outro ramal",
            TipoHistoricoLigacao.CaixaPostal => "Caixa postal",
            TipoHistoricoLigacao.Recusada => "Recusada",
            TipoHistoricoLigacao.NaoAtendida => "Não atendida",
            // v2.3.6 — badge da lista precisa ficar curto, igual aos demais tipos (Recebida/Perdida/
            // Realizada/Recusada/Não atendida). O texto completo "Cancelada pelo operador" fica em
            // TipoTextoDetalhado, usado em tooltip/detalhes.
            TipoHistoricoLigacao.Cancelada => "Cancelada",
            _ => Tipo.ToString()
        };

        // v2.3.6 — versão longa, só para tooltip/"Ver detalhes da chamada" — nunca no badge da lista.
        [JsonIgnore]
        public string TipoTextoDetalhado => Tipo switch
        {
            TipoHistoricoLigacao.Cancelada => "Cancelada pelo operador",
            TipoHistoricoLigacao.Perdida when EhDesistenciaRapida => "Cliente desligou enquanto os ramais ainda tocavam",
            TipoHistoricoLigacao.Perdida when EhAbandonoFila => "Cliente desligou enquanto aguardava na fila",
            TipoHistoricoLigacao.Perdida => "Tocou até o fim e ninguém atendeu",
            _ => TipoTexto
        };

        [JsonIgnore]
        public Brush CorTipo => EhForaDoHorario ? new SolidColorBrush(Color.FromRgb(109, 40, 217))
            : EhEventoTecnico ? new SolidColorBrush(Color.FromRgb(100, 116, 139)) : Tipo switch
        {
            TipoHistoricoLigacao.Realizada => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            TipoHistoricoLigacao.Recebida => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            TipoHistoricoLigacao.Perdida => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            TipoHistoricoLigacao.CaixaPostal => new SolidColorBrush(Color.FromRgb(161, 98, 7)),
            TipoHistoricoLigacao.Recusada => new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            TipoHistoricoLigacao.NaoAtendida => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            // v2.3.6 — cinza-azulado neutro, de propósito distinto do vermelho de Perdida/Recusada
            // e do amarelo de NaoAtendida: Cancelada não é uma falha do cliente nem um timeout.
            TipoHistoricoLigacao.Cancelada => new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            _ => Brushes.Gray
        };

        [JsonIgnore]
        public Brush CorTipoFundo => EhForaDoHorario ? new SolidColorBrush(Color.FromRgb(245, 243, 255))
            : EhEventoTecnico ? new SolidColorBrush(Color.FromRgb(241, 245, 249)) : Tipo switch
        {
            TipoHistoricoLigacao.Realizada => new SolidColorBrush(Color.FromRgb(240, 253, 244)),
            TipoHistoricoLigacao.Recebida => new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            TipoHistoricoLigacao.Perdida => new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
            TipoHistoricoLigacao.CaixaPostal => new SolidColorBrush(Color.FromRgb(254, 249, 195)),
            TipoHistoricoLigacao.Recusada => new SolidColorBrush(Color.FromRgb(254, 226, 226)),
            TipoHistoricoLigacao.NaoAtendida => new SolidColorBrush(Color.FromRgb(255, 251, 235)),
            TipoHistoricoLigacao.Cancelada => new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            _ => new SolidColorBrush(Color.FromRgb(241, 245, 249))
        };

        // v2.5.0 — destaque das "Não atendidas" (Perdida, Cliente desistiu, Abandono na fila) na
        // lista do Histórico: selo e ícone em vermelho cheio com texto branco, nome em vermelho e
        // faixa vermelha à esquerda da linha. Os demais tipos usam as cores suaves de sempre.
        private static readonly Brush _vermelhoNaoAtendida = Congelar(new SolidColorBrush(Color.FromRgb(220, 38, 38)));
        private static readonly Brush _vermelhoNomeNaoAtendida = Congelar(new SolidColorBrush(Color.FromRgb(185, 28, 28)));
        private static readonly Brush _fundoLinhaNaoAtendida = Congelar(new SolidColorBrush(Color.FromRgb(255, 245, 245)));
        private static readonly Brush _textoNomePadrao = Congelar(new SolidColorBrush(Color.FromRgb(16, 24, 40)));
        private static Brush Congelar(Brush b) { b.Freeze(); return b; }

        [JsonIgnore]
        public bool EhNaoAtendidaEntrada => NaoAtendidasPolicy.EhNaoAtendida(this);

        [JsonIgnore]
        public Brush CorSeloTexto => EhNaoAtendidaEntrada ? Brushes.White : CorTipo;

        [JsonIgnore]
        public Brush CorSeloFundo => EhNaoAtendidaEntrada ? _vermelhoNaoAtendida : CorTipoFundo;

        [JsonIgnore]
        public Brush CorNome => EhNaoAtendidaEntrada ? _vermelhoNomeNaoAtendida : _textoNomePadrao;

        [JsonIgnore]
        public Brush CorFaixa => EhNaoAtendidaEntrada ? _vermelhoNaoAtendida : Brushes.Transparent;

        [JsonIgnore]
        public Brush CorFundoLinha => EhNaoAtendidaEntrada ? _fundoLinhaNaoAtendida : Brushes.Transparent;

        [JsonIgnore]
        public string RamalExibido
        {
            get
            {
                if (EhChamadaRealizadaOuFalhaDeSaida())
                {
                    if (!string.IsNullOrWhiteSpace(RamalOrigem) && EhRamalString(RamalOrigem))
                        return NomeOuRamal(RamalOrigem);
                    return string.Empty;
                }
                if (Tipo == TipoHistoricoLigacao.CaixaPostal)
                    return EhForaDoHorario
                        ? "Mensagem automática + caixa postal"
                        : string.Empty; // TipoTexto already says "Caixa postal"
                // Recebida / Perdida / NaoAtendidaNesseRamal: show who answered (or dest ramal)
                if (!string.IsNullOrWhiteSpace(RamalAtendeu))
                    return NomeOuRamal(RamalAtendeu);
                // v2.5.0 — só o PABX atendeu (URA/anúncio): diz isso em vez de deixar o campo
                // vazio ou cair no RamalDestino, que aqui não é ninguém que tenha atendido.
                if (Tipo == TipoHistoricoLigacao.Recebida && !string.IsNullOrWhiteSpace(AtendimentoAutomatico))
                {
                    if (EhForaDoHorario) return "Mensagem automática";
                    if (EhEventoTecnico) return "Retorno da própria ligação (sem cliente)";
                    return string.Equals(AtendimentoAutomatico, "Anuncio", StringComparison.OrdinalIgnoreCase)
                        ? "Mensagem automática (sem atendente)"
                        : "URA (sem atendente)";
                }
                // Queue-abandoned call: RamalDestino is only the last ring attempt target,
                // not proof the agent missed or rejected the call — hide to avoid false attribution.
                if (Tipo == TipoHistoricoLigacao.Perdida &&
                    string.Equals(OrigemSaida, "Queue", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                if (!string.IsNullOrWhiteSpace(RamalDestino) && EhRamalString(RamalDestino))
                    return NomeOuRamal(RamalDestino);
                return string.Empty;
            }
        }

        private static bool EhRamalString(string s)
            => !string.IsNullOrWhiteSpace(s) && s.All(char.IsDigit) && s.Length >= 2 && s.Length <= 5;

        private static string NomeOuRamal(string ramal)
        {
            if (string.IsNullOrWhiteSpace(ramal)) return string.Empty;
            try
            {
                var nome = ContatoStorageService.ResolverNomePorNumero(ramal);
                // Contact found → "Sabrina Pereira (102)"; not found → "Ramal 102"
                return string.Equals(nome, ramal, StringComparison.OrdinalIgnoreCase)
                    ? $"Ramal {ramal}"
                    : $"{nome} ({ramal})";
            }
            catch { return $"Ramal {ramal}"; }
        }

        [JsonIgnore]
        public bool TemGravacao => !string.IsNullOrWhiteSpace(GravacaoUrl) || !string.IsNullOrWhiteSpace(GravacaoArquivo);

        [JsonIgnore]
        public Visibility VisibilidadeGravacao => TemGravacao ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility VisibilidadeRamal =>
            string.IsNullOrWhiteSpace(RamalExibido) ? Visibility.Collapsed : Visibility.Visible;

        private static readonly System.Collections.Generic.HashSet<string> _labelsSipTecnicas =
            new(System.StringComparer.OrdinalIgnoreCase)
            { "SIP", "PJSIP", "DAHDI", "Local", "Macro", "Desconhecido" };

        private static bool EhLabelSipTecnico(string s)
            => !string.IsNullOrWhiteSpace(s) && _labelsSipTecnicas.Contains(s.Trim());

        private static string FiltrarLabelSip(string nome)
            => EhLabelSipTecnico(nome) ? "Chamada do sistema" : (nome ?? string.Empty);

        private static string LimparPrefixoRota(string numero)
        {
            var semRota = WavenVoIP.DialPlanService.RemoverPrefixoDeRota(numero ?? string.Empty);
            return Services.PhoneNumberNormalizer.NormalizeForDisplay(semRota);
        }

        private static string DetectarSaidaPeloPrefixo(string numero)
        {
            var n = WavenVoIP.DialPlanService.RemoverDuplicacaoSequencial(numero ?? string.Empty);
            if (string.IsNullOrWhiteSpace(n)) return "Saída não identificada";
            if (WavenVoIP.DialPlanService.EhRamalInterno(n)) return "Ramal interno";
            return n[0] switch
            {
                '1' => "Operadora",
                '2' => "WhatsApp TIM",
                '3' => "WhatsApp Vivo",
                _ => "Saída não identificada"
            };
        }
    }
}
