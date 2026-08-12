using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Linq;
using WavenVoIP.Models;
using WavenVoIP;

namespace WavenVoIP.Services
{
    public static class HistoricoStorageService
    {
        private static readonly string _path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "historico.json");

        private static readonly object _saveLock = new object();

        private static void Log(string msg, LogLevel level = LogLevel.INFO)
        {
            try { LogHelper.Cdr(msg, level); }
            catch { }
        }

        private static string MascararNumeroLog(string numero)
        {
            var n = new string((numero ?? string.Empty).Where(char.IsDigit).ToArray());
            if (n.Length <= 4) return new string('*', n.Length);
            return n.Substring(0, 2) + new string('*', n.Length - 4) + n.Substring(n.Length - 2);
        }

        // v2.3.6 — chamado com muita frequência (todo refresh do Histórico, todo sync de CDR); só
        // loga o tempo quando passa de um limiar pequeno, pra não poluir o log com centenas de
        // linhas de leituras normais (poucos ms) — só o que realmente vale a pena investigar.
        private const int LimiarLogLentoMs = 20;

        public static List<HistoricoLigacaoItem> Carregar()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(_path))
                    return new List<HistoricoLigacaoItem>();

                var json = File.ReadAllText(_path);
                var itens = JsonSerializer.Deserialize<List<HistoricoLigacaoItem>>(json) ?? new List<HistoricoLigacaoItem>();
                if (sw.ElapsedMilliseconds >= LimiarLogLentoMs)
                    Log($"HISTORICO_JSON_READ_TIMING ms={sw.ElapsedMilliseconds} itens={itens.Count} bytes={json.Length}");
                return itens;
            }
            catch
            {
                return new List<HistoricoLigacaoItem>();
            }
        }

        // v2.3.6 — leitura+escrita SEM lock próprio: só deve ser chamada de dentro de um bloco que já
        // segura _saveLock (ver ExecutarAtomico). Evita reentrância no lock (não é reentrant-safe aqui
        // por design simples) e mantém Carregar()/Salvar() públicos livres para uso avulso (leitura de
        // tela, relatórios) sem pagar o custo de lock nesses casos, que não precisam de atomicidade.
        private static List<HistoricoLigacaoItem> CarregarSemLock()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<HistoricoLigacaoItem>();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<List<HistoricoLigacaoItem>>(json) ?? new List<HistoricoLigacaoItem>();
            }
            catch
            {
                return new List<HistoricoLigacaoItem>();
            }
        }

        private static void SalvarSemLock(List<HistoricoLigacaoItem> itens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(itens, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }

        // v2.3.6 — elimina a condição de corrida entre RegistrarHistorico (grava o stub Cancelada no
        // instante em que o operador cancela) e MesclarCdr (sync automático a cada poucos segundos):
        // antes, cada um fazia seu próprio Carregar()→processa→Salvar() sem nenhuma trava cobrindo o
        // ciclo inteiro (só a escrita final tinha lock) — se os dois ciclos se intercalassem, o que
        // salvasse por último sobrescrevia o outro (lost update), apagando o stub recém-criado.
        // ExecutarAtomico garante que leitura+mutação+escrita rodem como uma única seção crítica.
        // Reaproveita o mesmo _saveLock do Salvar() público (em vez de uma trava separada) — assim
        // nenhum Salvar() avulso de outro trecho do código consegue intercalar sua própria escrita no
        // meio de um ciclo atômico. É um lock curto e só em memória/arquivo local (sem HTTP dentro),
        // então não trava a UI nem atrasa o SIP — RegistrarHistorico continua respondendo em milissegundos.
        public static TResult ExecutarAtomico<TResult>(
            Func<List<HistoricoLigacaoItem>, (List<HistoricoLigacaoItem> paraSalvar, TResult resultado)> mutador)
        {
            lock (_saveLock)
            {
                var itens = CarregarSemLock();
                var (paraSalvar, resultado) = mutador(itens);
                SalvarSemLock(paraSalvar);
                return resultado;
            }
        }

        public static List<HistoricoLigacaoItem> CarregarComRetencao(int diasRetencao)
        {
            // Lê o arquivo uma única vez — evita double-read (LimparAntigas + Carregar)
            try
            {
                if (diasRetencao <= 0) diasRetencao = 7;
                var itens = Carregar();
                var limite = System.DateTime.Now.AddDays(-diasRetencao);
                var filtrados = itens.Where(i => i.DataHora >= limite).ToList();
                if (filtrados.Count != itens.Count)
                {
                    Salvar(filtrados);
                    return filtrados;
                }
                return itens;
            }
            catch
            {
                return new List<HistoricoLigacaoItem>();
            }
        }

        public static void LimparAntigas(int diasRetencao)
        {
            try
            {
                if (diasRetencao <= 0) diasRetencao = 7;
                var itens = Carregar();
                var limite = System.DateTime.Now.AddDays(-diasRetencao);
                var filtrados = itens.Where(i => i.DataHora >= limite).ToList();
                if (filtrados.Count != itens.Count)
                    Salvar(filtrados);
            }
            catch
            {
            }
        }

        public static void Salvar(List<HistoricoLigacaoItem> itens)
        {
            var sw = Stopwatch.StartNew();
            lock (_saveLock)
            {
                SalvarSemLock(itens);
            }
            if (sw.ElapsedMilliseconds >= LimiarLogLentoMs)
                Log($"HISTORICO_JSON_WRITE_TIMING ms={sw.ElapsedMilliseconds} itens={itens.Count}");
        }

        // Janela de correlação número+horário usada tanto para "enriquecer" um stub Cancelada com o
        // CDR correspondente quanto (antes) pelo RemoveAll genérico — mesmo valor de sempre (±2 min).
        private const double JanelaCorrelacaoMinutos = 2;

        private static bool MesmoNumero(string numA, string numB)
            => string.Equals(
                PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(numA ?? string.Empty)),
                PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(numB ?? string.Empty)),
                StringComparison.OrdinalIgnoreCase);

        // Merges CDR-sourced items into local history, updating existing records and adding new ones.
        // Existing CDR items (matched by UniqueId) are REPLACED with fresh data to fix concatenated
        // numbers, bad URLs and stale ramal info from previous syncs.
        // Returns count of newly added items.
        public static int MesclarCdr(List<HistoricoLigacaoItem> itensCdr)
        {
            if (itensCdr == null || itensCdr.Count == 0) return 0;
            var swTotal = Stopwatch.StartNew();

            // v2.3.6 — leitura, mutação e escrita agora rodam dentro de UMA seção crítica só
            // (ExecutarAtomico), com o mesmo lock usado por RegistrarHistorico. Antes deste fix,
            // MesclarCdr fazia seu próprio Carregar() aqui, independente do Carregar() feito minutos/
            // segundos antes por IssabelCdrService.SincronizarAsync para montar locaisComResultadoSip
            // — se um stub Cancelada fosse gravado por RegistrarHistorico bem no meio dessas duas
            // leituras, a reconciliação não o via (candidatos=0, CDR virava NaoAtendida) mas o
            // RemoveAll abaixo via sim, e apagava o stub por achar que tinha sido "substituído" pelo
            // CDR errado. Ver bloco HISTORY_CANCELLED_STUB_* abaixo — agora a proteção não depende
            // mais de qual leitura a reconciliação usou: mesmo que ela erre, este merge é a rede de
            // segurança final e nunca deixa um CanceladaPeloOperador=true virar outra coisa.
            var (novos, total) = ExecutarAtomico<(int novos, int total)>(existentes =>
            {
                Log($"HISTORY_MERGE_SNAPSHOT itensExistentes={existentes.Count} itensCdrRecebidos={itensCdr.Count}");

                // ---- Proteção da Cancelada: consome o item CDR correspondente por enriquecimento,
                // em vez de deixar o loop abaixo criar uma segunda linha (NaoAtendida) que o RemoveAll
                // usaria depois como desculpa pra apagar o stub local. ----
                var cdrConsumidos = new HashSet<HistoricoLigacaoItem>();
                foreach (var stub in existentes.Where(i => !i.FonteCdr && i.CanceladaPeloOperador))
                {
                    var match = itensCdr.FirstOrDefault(c =>
                        !cdrConsumidos.Contains(c) &&
                        MesmoNumero(c.Numero, stub.Numero) &&
                        Math.Abs((c.DataHora - stub.DataHora).TotalMinutes) <= JanelaCorrelacaoMinutos);

                    if (match == null)
                    {
                        Log($"HISTORY_CANCELLED_STUB_PENDING numero={MascararNumeroLog(stub.Numero)} stubId={stub.Id} " +
                            $"dataHora={stub.DataHora:yyyy-MM-dd HH:mm:ss} motivo=sem_cdr_correspondente_ainda");
                        continue;
                    }

                    Log($"HISTORY_CANCELLED_STUB_FOUND numero={MascararNumeroLog(stub.Numero)} stubId={stub.Id} " +
                        $"cdrUid={match.UniqueId} cdrTipo={match.Tipo} cdrDataHora={match.DataHora:yyyy-MM-dd HH:mm:ss}");

                    stub.FonteCdr = true;
                    if (!string.IsNullOrWhiteSpace(match.UniqueId)) stub.UniqueId = match.UniqueId;
                    if (!string.IsNullOrWhiteSpace(match.LinkedId)) stub.LinkedId = match.LinkedId;
                    if (string.IsNullOrWhiteSpace(stub.OrigemSaida)) stub.OrigemSaida = match.OrigemSaida;
                    if (string.IsNullOrWhiteSpace(stub.CanalEntrada)) stub.CanalEntrada = match.CanalEntrada;
                    if (string.IsNullOrWhiteSpace(stub.RamalOrigem)) stub.RamalOrigem = match.RamalOrigem;
                    if (string.IsNullOrWhiteSpace(stub.RamalDestino)) stub.RamalDestino = match.RamalDestino;
                    if (string.IsNullOrWhiteSpace(stub.GravacaoArquivo)) stub.GravacaoArquivo = match.GravacaoArquivo;
                    if (string.IsNullOrWhiteSpace(stub.GravacaoUrl)) stub.GravacaoUrl = match.GravacaoUrl;
                    if (string.IsNullOrWhiteSpace(stub.Duracao) || stub.Duracao == "00:00") stub.Duracao = match.Duracao;
                    // Nunca mexe em Tipo/CanceladaPeloOperador — já são Cancelada/true, e é
                    // exatamente isso que esta função existe pra preservar.

                    cdrConsumidos.Add(match);
                    Log($"HISTORY_CANCELLED_STUB_ENRICHED numero={MascararNumeroLog(stub.Numero)} stubId={stub.Id} " +
                        $"cdrUid={stub.UniqueId} tipo={stub.Tipo} canceladaPeloOperador={stub.CanceladaPeloOperador} fonteCdr={stub.FonteCdr}");
                }

                var itensCdrRestantes = cdrConsumidos.Count == 0
                    ? itensCdr
                    : itensCdr.Where(c => !cdrConsumidos.Contains(c)).ToList();

                // Build O(1) index of existing CDR items for update lookup.
                // TryAdd instead of ToDictionary: Asterisk linkedid/uniqueid can repeat in queues and
                // transfers — crashing the entire sync for a duplicate key is unacceptable.
                var indicePorUid = new Dictionary<string, HistoricoLigacaoItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in existentes.Where(i => !string.IsNullOrWhiteSpace(i.UniqueId) && i.FonteCdr))
                {
                    if (!indicePorUid.TryAdd(e.UniqueId, e))
                        Log($"CDR_DUPLICATE_KEY_DETECTED uid={e.UniqueId} numero={e.Numero} data={e.DataHora:yyyy-MM-dd HH:mm:ss} acao=ignorado");
                }

                var novosCount = 0;
                foreach (var item in itensCdrRestantes)
                {
                    if (string.IsNullOrWhiteSpace(item.UniqueId) || !indicePorUid.ContainsKey(item.UniqueId))
                    {
                        existentes.Add(item);
                        novosCount++;
                        // Track in index so a second occurrence in the same itensCdr batch
                        // updates rather than duplicates the entry.
                        if (!string.IsNullOrWhiteSpace(item.UniqueId))
                            indicePorUid.TryAdd(item.UniqueId, item);
                    }
                    else
                    {
                        // Overwrite stale fields with fresh CDR data (fixes concatenated numbers, 404 URLs, etc.)
                        var existente = indicePorUid[item.UniqueId];

                        // v2.3.6 — segunda camada de proteção (a primeira é a reconciliação em
                        // IssabelCdrService.SincronizarAsync, que já deveria ter propagado Tipo=Cancelada
                        // pro item fresco): mesmo que algo dê errado lá, um registro já marcado
                        // CanceladaPeloOperador=true NUNCA tem seu Tipo trocado por aqui — é a prova
                        // local de que o operador cancelou, mais confiável que qualquer disposition
                        // genérico do CDR (o tronco WAVOIP pode registrar ANSWERED mesmo sem atendimento
                        // real — ver comentário na reconciliação).
                        var bloquearTipo = existente.CanceladaPeloOperador && item.Tipo != TipoHistoricoLigacao.Cancelada;
                        // v2.4.0 — investigação de log excessivo: quando bloqueado=true, esta
                        // transição é PERMANENTEMENTE recusada (registro protegido por
                        // CanceladaPeloOperador — ver !bloquearTipo abaixo, que nunca aplica o
                        // Tipo do CDR nesse caso) e o CDR insiste em reenviar o mesmo Tipo
                        // divergente a cada sync (a cada 2-3s) — sem gate, isso gerava a MESMA
                        // linha, para o MESMO registro já finalizado, repetida indefinidamente
                        // enquanto o item existir no histórico (medido em teste real: ~42 mil
                        // linhas em 31 min de app ocioso, maior fonte isolada de volume de
                        // cdr_sync.log). Não muda NENHUM dado — só o volume de log. Uma
                        // transição REAL (bloqueado=false) sempre loga; a tentativa bloqueada
                        // (sem efeito algum no registro) só loga com "Logs detalhados" ativado.
                        if (existente.Tipo != item.Tipo && (!bloquearTipo || LogHelper.IsDetailedEnabled))
                            Log($"HISTORY_TYPE_TRANSITION id={existente.Id} uid={item.UniqueId} from={existente.Tipo} " +
                                $"to={item.Tipo} method=MesclarCdr fonteCdr={item.FonteCdr} bloqueado={bloquearTipo}");

                        existente.Numero          = item.Numero;
                        existente.Nome            = item.Nome;
                        if (!bloquearTipo) existente.Tipo = item.Tipo;
                        existente.Duracao         = item.Duracao;
                        existente.OrigemSaida     = item.OrigemSaida;
                        existente.CanalEntrada    = item.CanalEntrada;
                        existente.RamalOrigem     = item.RamalOrigem;
                        existente.RamalDestino    = item.RamalDestino;
                        existente.RamalAtendeu    = item.RamalAtendeu;
                        existente.GravacaoArquivo = item.GravacaoArquivo;
                        existente.GravacaoUrl     = item.GravacaoUrl;
                        if (item.CanceladaPeloOperador) existente.CanceladaPeloOperador = true;
                    }
                }

                // Remove locally-tracked items superseded by a CDR record (same external number, ±2 min).
                // Pré-normaliza os números do CDR em Dictionary<numero, List<DateTime>> para lookup O(1),
                // evitando O(n*m) do RemoveAll().Any() original.
                var cdrPorNumero = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
                var cdrRamalPorNumero = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in itensCdrRestantes)
                {
                    if (string.IsNullOrWhiteSpace(c.Numero)) continue;
                    var numC = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(c.Numero));
                    if (DialPlanService.EhRamalInterno(numC))
                    {
                        if (!cdrRamalPorNumero.TryGetValue(numC, out var datasR))
                            cdrRamalPorNumero[numC] = datasR = new List<DateTime>();
                        datasR.Add(c.DataHora);
                    }
                    else
                    {
                        if (numC.Length < 8) continue;
                        if (!cdrPorNumero.TryGetValue(numC, out var datas))
                            cdrPorNumero[numC] = datas = new List<DateTime>();
                        datas.Add(c.DataHora);
                    }
                }

                existentes.RemoveAll(e =>
                {
                    if (e.FonteCdr || string.IsNullOrWhiteSpace(e.Numero)) return false;

                    // v2.3.6 — rede de segurança final: um stub CanceladaPeloOperador=true NUNCA é
                    // removido por aqui. Se ele tinha um CDR correspondente no lote, já foi consumido
                    // por enriquecimento (bloco HISTORY_CANCELLED_STUB_* acima) e nem chega mais neste
                    // RemoveAll com FonteCdr=false. Se não tinha, fica pendente pra próxima sync — mas
                    // jamais é apagado só por coincidência de número+horário com um CDR não relacionado.
                    if (e.CanceladaPeloOperador)
                    {
                        var numE0 = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(e.Numero));
                        var teriaSidoRemovido =
                            (cdrRamalPorNumero.TryGetValue(numE0, out var datasR0) && datasR0.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= JanelaCorrelacaoMinutos)) ||
                            (cdrPorNumero.TryGetValue(numE0, out var datas0) && datas0.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= JanelaCorrelacaoMinutos));
                        if (teriaSidoRemovido)
                            Log($"HISTORY_CANCELLED_STUB_REMOVE_BLOCKED numero={MascararNumeroLog(e.Numero)} " +
                                $"stubId={e.Id} canceladaPeloOperador=true");
                        return false;
                    }

                    var numE = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(e.Numero));
                    bool substituida;
                    if (DialPlanService.EhRamalInterno(numE))
                    {
                        substituida = cdrRamalPorNumero.TryGetValue(numE, out var datasR) &&
                                      datasR.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= JanelaCorrelacaoMinutos);
                    }
                    else if (numE.Length < 8)
                    {
                        substituida = false;
                    }
                    else
                    {
                        substituida = cdrPorNumero.TryGetValue(numE, out var datas) &&
                                      datas.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= JanelaCorrelacaoMinutos);
                    }

                    if (substituida)
                        Log($"HISTORY_TEMP_ENTRY_REPLACED numero={numE} tipoTemporario={e.Tipo}");
                    return substituida;
                });

                // Deduplicate by UniqueId before saving. Prior buggy syncs accumulated multiple copies
                // of the same CDR record in the JSON. The first occurrence of each UID in existentes
                // is the one that received the fresh CDR update above — keep it, discard the rest.
                var seenUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var semDuplicatas = existentes
                    .Where(i => string.IsNullOrWhiteSpace(i.UniqueId) || seenUids.Add(i.UniqueId))
                    .ToList();
                if (semDuplicatas.Count != existentes.Count)
                    Log($"CDR_DEDUP_SAVE_CLEANUP removidos={existentes.Count - semDuplicatas.Count} antes={existentes.Count} depois={semDuplicatas.Count}");

                // v2.3.6 — poda entradas Perdida/NaoAtendidaNesseRamal órfãs (bug real encontrado: uma
                // sync anterior, ANTES de um retorno bem-sucedido do mesmo cliente existir no CDR, cria
                // uma entrada separada para a perna de tentativa de fila; uma sync mais tardia já
                // reconhece essa perna como parte de uma chamada respondida minutos depois, mas o loop
                // acima só ATUALIZA/SOMA uids presentes no lote fresco — nunca remove um uid que "sumiu"
                // do lote por ter sido corretamente suprimido numa sync melhor informada). Reaproveita a
                // MESMA lógica de supressão já usada dentro do próprio sync do CDR (IssabelCdrService),
                // aplicada aqui só à janela recente — nunca aos 5000 registros inteiros, então o custo
                // extra fica preso a poucas dezenas de itens mesmo num histórico grande.
                var jaVerificarPodaEmMinutos = 20; // >> janela de correlação de 180s, com folga generosa
                var corteRecente = DateTime.Now.AddMinutes(-jaVerificarPodaEmMinutos);
                var recentes = semDuplicatas.Where(i => i.DataHora >= corteRecente).ToList();
                var antigos  = semDuplicatas.Where(i => i.DataHora <  corteRecente).ToList();
                if (recentes.Count > 1)
                {
                    var recentesPodados = IssabelCdrService.SuprimirPerdidasAtendidasPorOutroRamal(recentes);
                    if (recentesPodados.Count != recentes.Count)
                        Log($"HISTORY_STALE_MISSED_PRUNED removidos={recentes.Count - recentesPodados.Count}");
                    semDuplicatas = antigos.Concat(recentesPodados).ToList();
                }

                var listaFinal = semDuplicatas.OrderByDescending(i => i.DataHora).Take(5000).ToList();
                return (listaFinal, (novosCount, listaFinal.Count));
            });

            Log($"MESCLAR_CDR_TIMING ms={swTotal.ElapsedMilliseconds} novos={novos} total={total}");
            return novos;
        }
    }
}
