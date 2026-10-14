using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    public static class ContatoStorageService
    {
        private static string _path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "contatos.json");

        private static List<Contato>? _cache;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly object _cacheLock = new object();

        // v2.5.0 — contatos salvos desapareciam (caso real: "Suporte Dlink", 12→13/10/2026).
        // Várias sincronizações (Google, empresa, Waven API, deduplicação da tela) leem e regravam
        // este arquivo ao mesmo tempo. A gravação não era atômica e a leitura, quando encontrava o
        // arquivo em uso ("being used by another process"), devolvia uma LISTA VAZIA em silêncio —
        // a sincronização seguinte reconstruía só os contatos do Google/empresa e gravava por
        // cima, apagando os contatos salvos à mão.
        //  • um único lock para ler e gravar; gravação em arquivo temporário + troca atômica;
        //  • leitura com novas tentativas; se ainda assim falhar, devolve a última versão lida com
        //    sucesso — nunca uma lista vazia;
        //  • as sincronizações automáticas fazem ler→alterar→gravar dentro do mesmo lock.
        private static readonly object _ioLock = new object();
        private static string? _ultimoJsonBom;
        private static readonly JsonSerializerOptions _jsonGravacao = new JsonSerializerOptions { WriteIndented = true };

        public static List<Contato> Carregar()
        {
            lock (_ioLock)
            {
                if (!File.Exists(_path))
                    return new List<Contato>();

                Exception? erro = null;
                for (int tentativa = 0; tentativa < 6; tentativa++)
                {
                    try
                    {
                        string json;
                        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            json = sr.ReadToEnd();
                        var result = JsonSerializer.Deserialize<List<Contato>>(json);
                        if (result == null) throw new InvalidDataException("arquivo de contatos vazio");
                        _ultimoJsonBom = json;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        erro = ex;
                        System.Threading.Thread.Sleep(40 * (tentativa + 1));
                    }
                }

                Log($"CONTACTS_LOAD_ERROR | {erro?.Message} | usando_ultima_leitura_valida={_ultimoJsonBom != null}");
                if (_ultimoJsonBom != null)
                {
                    try { return JsonSerializer.Deserialize<List<Contato>>(_ultimoJsonBom) ?? new List<Contato>(); } catch { }
                }
                // Sem nenhuma leitura válida anterior: falha explícita. Devolver lista vazia aqui
                // faria o chamador gravar por cima e apagar os contatos.
                throw new IOException("Não foi possível ler o arquivo de contatos.", erro);
            }
        }

        public static void Salvar(List<Contato> contatos)
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(contatos, _jsonGravacao);
                // v2.5.0 — desempenho: as sincronizações automáticas gravavam o arquivo inteiro
                // (~1 MB) a cada ciclo mesmo sem nenhuma mudança. Conteúdo igual = não regrava,
                // não vence o cache de nomes e não gera linha de log.
                try
                {
                    if (File.Exists(_path) && string.Equals(File.ReadAllText(_path), json, StringComparison.Ordinal))
                    {
                        _ultimoJsonBom = json;
                        return;
                    }
                }
                catch { }
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                _ultimoJsonBom = json;
            }
            // sem lock aqui: quem resolve nomes segura _cacheLock e depois lê o arquivo (_ioLock);
            // pegar _cacheLock com _ioLock na mão inverteria a ordem (risco de deadlock)
            _cacheExpiry = DateTime.MinValue;
            Log($"CONTACT_SAVE_OK | count={contatos.Count}");
        }

        // Ler → alterar → gravar sem que outra sincronização entre no meio.
        public static T ComArquivoTravado<T>(Func<T> operacao)
        {
            lock (_ioLock) return operacao();
        }

        public static string ResolverNomePorNumero(string numero)
        {
            try
            {
                var n = SomenteDigitos(numero);
                if (string.IsNullOrWhiteSpace(n)) return numero;

                List<Contato> contatos;
                lock (_cacheLock)
                {
                    if (_cache == null || DateTime.Now > _cacheExpiry)
                    {
                        _cache = Carregar();
                        _cacheExpiry = DateTime.Now.AddSeconds(300);
                    }
                    contatos = _cache;
                }

                // Build normalized variants: strips 55 prefix and adds/removes 9th digit
                var nNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(n);
                var variantes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { n, nNorm };
                // Variant without 9th digit (11-digit mobile → 10-digit)
                if (nNorm.Length == 11 && nNorm[2] == '9')
                    variantes.Add(nNorm.Substring(0, 2) + nNorm.Substring(3));
                // Variant with 9th digit (10-digit → 11-digit)
                if (nNorm.Length == 10 && nNorm[2] >= '6' && nNorm[2] <= '9')
                    variantes.Add(nNorm.Substring(0, 2) + "9" + nNorm.Substring(2));

                // Exact/normalized match first
                var contato = contatos.FirstOrDefault(c =>
                {
                    var cn = SomenteDigitos(c.Numero);
                    var cnNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(cn);
                    return variantes.Contains(cn) || variantes.Contains(cnNorm);
                });
                if (contato != null)
                {
                    // v2.5.0: sem log por consulta — roda para cada linha a cada refresh do Histórico
                    // (~30 MB/dia de log, com número e nome em claro).
                    return contato.Nome;
                }

                // Fallback: last 8 digits suffix match
                var ultimos8 = n.Length >= 8 ? n.Substring(n.Length - 8) : n;
                if (ultimos8.Length >= 8)
                    contato = contatos.FirstOrDefault(c =>
                    {
                        var cn = SomenteDigitos(c.Numero);
                        return cn.EndsWith(ultimos8, StringComparison.OrdinalIgnoreCase);
                    });

                if (contato != null)
                {
                    return contato.Nome;
                }

                return numero;
            }
            catch { return numero; }
        }

        public static bool ExisteNumero(string numero)
        {
            try
            {
                var n = SomenteDigitos(numero);
                if (string.IsNullOrWhiteSpace(n)) return false;
                var contatos = Carregar();
                return contatos.Any(c => SomenteDigitos(c.Numero) == n);
            }
            catch { return false; }
        }
        public static int SincronizarRamaisIssabel(IEnumerable<Contato> ramais)
            => ComArquivoTravado(() => SincronizarRamaisIssabelImpl(ramais));

        private static int SincronizarRamaisIssabelImpl(IEnumerable<Contato> ramais)
        {
            var contatos = Carregar();
            var alterados = 0;

            foreach (var ramal in ramais ?? Enumerable.Empty<Contato>())
            {
                var numero = SomenteDigitos(ramal.Numero);
                if (string.IsNullOrWhiteSpace(numero))
                    continue;

                var nome = string.IsNullOrWhiteSpace(ramal.Nome) ? numero : ramal.Nome.Trim();
                var existente = contatos.FirstOrDefault(c => SomenteDigitos(c.Numero) == numero);

                if (existente == null)
                {
                    contatos.Add(new Contato
                    {
                        Nome = nome,
                        Numero = numero,
                        Observacao = "Ramal Issabel (AMI)",
                        EhRamalIssabel = true,
                        AtualizadoEm = DateTime.Now
                    });
                    alterados++;
                }
                else
                {
                    if (!string.Equals(existente.Nome ?? string.Empty, nome, StringComparison.OrdinalIgnoreCase) ||
                        existente.EhRamalIssabel != true ||
                        !string.Equals(existente.Observacao ?? string.Empty, "Ramal Issabel (AMI)", StringComparison.OrdinalIgnoreCase))
                    {
                        existente.Nome = nome;
                        existente.Numero = numero;
                        existente.Observacao = "Ramal Issabel (AMI)";
                        existente.EhRamalIssabel = true;
                        existente.AtualizadoEm = DateTime.Now;
                        alterados++;
                    }
                }
            }

            // v2.5.0 — desempenho: sem ramal novo/alterado não regrava (esta ordenação é diferente
            // da usada pela importação do Google, e as duas regravavam o arquivo inteiro a cada minuto).
            if (alterados > 0)
                Salvar(contatos.OrderByDescending(c => c.EhRamalIssabel).ThenBy(c => SomenteDigitos(c.Numero)).ThenBy(c => c.Nome).ToList());
            return alterados;
        }

        public static int SincronizarContatosGoogle(IEnumerable<Contato> contatosGoogle)
            => ComArquivoTravado(() => SincronizarContatosGoogleImpl(contatosGoogle));

        private static int SincronizarContatosGoogleImpl(IEnumerable<Contato> contatosGoogle)
        {
            var contatos = Carregar();
            contatos.RemoveAll(c => c.FonteGoogle);

            var adicionados = 0;
            foreach (var g in contatosGoogle ?? Enumerable.Empty<Contato>())
            {
                var numeroRaw = SomenteDigitos(g.Numero);
                if (string.IsNullOrWhiteSpace(numeroRaw) || string.IsNullOrWhiteSpace(g.Nome))
                    continue;

                // Normaliza na importação: remove 55 e adiciona 9º dígito se necessário
                var numero = PhoneNumberNormalizer.NormalizeBrazilPhone(numeroRaw);
                if (string.IsNullOrWhiteSpace(numero)) numero = numeroRaw;

                // Já existe contato local/da empresa (não Google) com o mesmo número: os dois são o
                // mesmo contato. Vale o nome alterado por último (ver ContatoSyncCoordinator); o
                // contato do Google não é duplicado na lista.
                var localMesmoNumero = contatos.FirstOrDefault(c => !c.FonteGoogle &&
                    PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(c.Numero)) == numero);
                if (localMesmoNumero != null)
                {
                    if (!localMesmoNumero.EhRamalIssabel)
                        ContatoSyncCoordinator.Reconciliar(localMesmoNumero, g, Log);
                    continue;
                }

                // Se está na lista de suprimidos (tombstone), não reimportar
                if (ContactTombstoneService.EstaSuprimido(g.GoogleContactId, numero))
                {
                    LogHelper.Info($"GOOGLE_SYNC_SUPPRESSED_BY_TOMBSTONE | googleId={g.GoogleContactId} numero={numero} nome={g.Nome}");
                    continue;
                }

                // Se existe contato não-Google vinculado ao mesmo GoogleContactId (foi convertido para empresa),
                // não sobrescrever com dados antigos do Google
                if (!string.IsNullOrWhiteSpace(g.GoogleContactId) &&
                    contatos.Any(c => !c.FonteGoogle && c.GoogleContactId == g.GoogleContactId))
                {
                    LogHelper.Info($"GOOGLE_CONTACT_NOT_OVERWRITING_SHARED | googleId={g.GoogleContactId} nome={g.Nome}");
                    continue;
                }

                contatos.Add(new Contato
                {
                    Nome            = g.Nome,
                    Numero          = numero,
                    Observacao      = "Google Contacts",
                    FonteGoogle     = true,
                    AtualizadoEm    = g.AtualizadoEm ?? DateTime.Now,
                    GoogleContactId = g.GoogleContactId
                });
                adicionados++;
            }

            // O Google traz o mesmo número em mais de um cadastro: resolve aqui, antes de gravar
            // (antes a tela de contatos removia os repetidos e gravava o arquivo outra vez).
            var ordenado = contatos
                .OrderByDescending(c => c.EhRamalIssabel)
                .ThenByDescending(c => c.FonteGoogle == false)
                .ThenBy(c => c.Nome, StringComparer.Ordinal)
                .ToList();
            var (semRepetidos, removidos) = Deduplicar(ordenado, salvarSeAlterou: false);
            Salvar(semRepetidos
                .OrderByDescending(c => c.EhRamalIssabel)
                .ThenByDescending(c => c.FonteGoogle == false)
                .ThenBy(c => c.Nome)
                .ToList());
            return Math.Max(0, adicionados - removidos);
        }

        // ── Migração automática de contatos antigos ───────────────────────────────

        private static volatile bool _migracaoJaExecutada = false;

        public static (int migrados, int deduplicados, int suspeitos) MigrarContatosAntigos()
        {
            if (_migracaoJaExecutada) return (0, 0, 0);
            _migracaoJaExecutada = true;

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var ct = cts.Token;

                var contatos = Carregar();
                var migrados = 0;
                var deduplicados = 0;
                var suspeitos = 0;

                // Passo 1: normalizar números — 14+ dígitos são suspeitos, não alterar
                foreach (var c in contatos)
                {
                    if (ct.IsCancellationRequested) break;
                    if (c.EhRamalIssabel) continue;
                    var n = SomenteDigitos(c.Numero);
                    if (string.IsNullOrEmpty(n) || n.Length < 8 || n.StartsWith("0")) continue;

                    if (n.Length >= 14)
                    {
                        Log($"CONTACT_NUMBER_SUSPICIOUS nome={c.Nome} numero={n} len={n.Length}");
                        suspeitos++;
                        continue;
                    }

                    var nNorm = PhoneNumberNormalizer.NormalizeBrazilPhone(n);
                    if (!string.IsNullOrEmpty(nNorm) && !string.Equals(nNorm, n, StringComparison.Ordinal))
                    {
                        Log($"CONTACT_MIGRATED nome={c.Nome} de={n} para={nNorm}");
                        c.Numero = nNorm;
                        c.AtualizadoEm = DateTime.Now;
                        migrados++;
                    }
                }

                // Passo 2: deduplicar por número normalizado (só entre não-ramais)
                List<Contato> resultado;
                if (!ct.IsCancellationRequested)
                {
                    var ramais = contatos.Where(c => c.EhRamalIssabel).ToList();
                    var semNumero = contatos.Where(c => string.IsNullOrEmpty(SomenteDigitos(c.Numero))).ToList();
                    var comNumero = contatos.Where(c => !c.EhRamalIssabel && !string.IsNullOrEmpty(SomenteDigitos(c.Numero)));

                    resultado = new List<Contato>(ramais);
                    resultado.AddRange(semNumero);

                    foreach (var grupo in comNumero.GroupBy(c =>
                        PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(c.Numero))))
                    {
                        if (ct.IsCancellationRequested) break;
                        var lista = grupo.ToList();
                        if (lista.Count == 1) { resultado.Add(lista[0]); continue; }

                        // Prioridade: local (não Google) > Google
                        var vencedor = lista.FirstOrDefault(c => !c.FonteGoogle) ?? lista[0];
                        resultado.Add(vencedor);
                        foreach (var descartado in lista.Where(c => c != vencedor))
                        {
                            Log($"CONTACT_DEDUPLICATED descartado={descartado.Nome} numero={descartado.Numero} mantido={vencedor.Nome}");
                            deduplicados++;
                        }
                    }
                }
                else
                {
                    resultado = contatos;
                }

                if (migrados > 0 || deduplicados > 0)
                {
                    Salvar(resultado
                        .OrderByDescending(c => c.EhRamalIssabel)
                        .ThenByDescending(c => !c.FonteGoogle)
                        .ThenBy(c => c.Nome)
                        .ToList());
                }

                var sufixo = ct.IsCancellationRequested ? " (timeout)" : "";
                Log($"CONTACT_MIGRATION_DONE migrados={migrados} deduplicados={deduplicados} suspeitos={suspeitos} total={resultado.Count}{sufixo}");
                return (migrados, deduplicados, suspeitos);
            }
            catch (Exception ex)
            {
                Log($"CONTACT_MIGRATION_ERROR {ex.Message}");
                return (0, 0, 0);
            }
        }

        // ── Deduplicação reutilizável ─────────────────────────────────────────────

        /// <summary>
        /// Remove duplicatas da lista por número normalizado.
        /// Prioridade: local (não Google) > Google; mais recente por AtualizadoEm.
        /// Salva automaticamente se encontrou duplicatas e salvarSeAlterou=true.
        /// </summary>
        public static (List<Contato> resultado, int removidos) Deduplicar(
            List<Contato> contatos, bool salvarSeAlterou = false)
        {
            try
            {
                var ramais   = contatos.Where(c => c.EhRamalIssabel).ToList();
                var semNum   = contatos.Where(c => !c.EhRamalIssabel &&
                                  string.IsNullOrEmpty(SomenteDigitos(c.Numero))).ToList();
                var comNum   = contatos.Where(c => !c.EhRamalIssabel &&
                                  !string.IsNullOrEmpty(SomenteDigitos(c.Numero)));

                var resultado = new List<Contato>(ramais);
                resultado.AddRange(semNum);
                var removidos = 0;

                foreach (var grupo in comNum.GroupBy(c =>
                    PhoneNumberNormalizer.NormalizeBrazilPhone(SomenteDigitos(c.Numero))))
                {
                    var lista = grupo.ToList();
                    if (lista.Count == 1) { resultado.Add(lista[0]); continue; }

                    // Priority: non-Google first, then most recently updated
                    var vencedor = lista
                        .OrderBy(c => c.FonteGoogle ? 1 : 0)
                        .ThenByDescending(c => c.AtualizadoEm ?? DateTime.MinValue)
                        .First();

                    resultado.Add(vencedor);
                    // v2.5.0 — desempenho: eram 3 linhas de log por contato duplicado a cada
                    // passada (milhares por minuto); agora uma linha de resumo no final.
                    removidos += lista.Count - 1;
                }
                if (removidos > 0)
                    Log($"CONTACT_DUPLICATE_MERGED | removidos={removidos}");

                if (removidos > 0 && salvarSeAlterou)
                {
                    var ordenado = resultado
                        .OrderByDescending(c => c.EhRamalIssabel)
                        .ThenByDescending(c => !c.FonteGoogle)
                        .ThenBy(c => c.Nome)
                        .ToList();
                    // Salvar já vence o cache (_cacheExpiry). Não pegar _cacheLock aqui: esta função
                    // roda com _ioLock na mão (ComArquivoTravado) e quem resolve nomes faz o caminho
                    // inverso (_cacheLock → _ioLock) — as sincronizações travavam para sempre.
                    Salvar(ordenado);
                    return (ordenado, removidos);
                }

                return (resultado, removidos);
            }
            catch (Exception ex)
            {
                Log($"CONTACT_DEDUP_ERROR | {ex.Message}");
                return (contatos, 0);
            }
        }

        private static string SomenteDigitos(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            return new string(valor.Where(char.IsDigit).ToArray());
        }

        private static void Log(string msg)
        {
            try { LogHelper.Contacts(msg); }
            catch { }
        }
    }
}
