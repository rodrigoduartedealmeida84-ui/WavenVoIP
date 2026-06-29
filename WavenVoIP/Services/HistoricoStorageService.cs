using System;
using System.Collections.Generic;
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

        public static List<HistoricoLigacaoItem> Carregar()
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
            lock (_saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(itens, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
        }

        // Merges CDR-sourced items into local history, updating existing records and adding new ones.
        // Existing CDR items (matched by UniqueId) are REPLACED with fresh data to fix concatenated
        // numbers, bad URLs and stale ramal info from previous syncs.
        // Returns count of newly added items.
        public static int MesclarCdr(List<HistoricoLigacaoItem> itensCdr)
        {
            if (itensCdr == null || itensCdr.Count == 0) return 0;

            var existentes = Carregar();

            // Build O(1) index of existing CDR items for update lookup.
            // TryAdd instead of ToDictionary: Asterisk linkedid/uniqueid can repeat in queues and
            // transfers — crashing the entire sync for a duplicate key is unacceptable.
            var indicePorUid = new Dictionary<string, HistoricoLigacaoItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in existentes.Where(i => !string.IsNullOrWhiteSpace(i.UniqueId) && i.FonteCdr))
            {
                if (!indicePorUid.TryAdd(e.UniqueId, e))
                    Log($"CDR_DUPLICATE_KEY_DETECTED uid={e.UniqueId} numero={e.Numero} data={e.DataHora:yyyy-MM-dd HH:mm:ss} acao=ignorado");
            }

            var novos = 0;
            foreach (var item in itensCdr)
            {
                if (string.IsNullOrWhiteSpace(item.UniqueId) || !indicePorUid.ContainsKey(item.UniqueId))
                {
                    existentes.Add(item);
                    novos++;
                    // Track in index so a second occurrence in the same itensCdr batch
                    // updates rather than duplicates the entry.
                    if (!string.IsNullOrWhiteSpace(item.UniqueId))
                        indicePorUid.TryAdd(item.UniqueId, item);
                }
                else
                {
                    // Overwrite stale fields with fresh CDR data (fixes concatenated numbers, 404 URLs, etc.)
                    var existente = indicePorUid[item.UniqueId];
                    existente.Numero          = item.Numero;
                    existente.Nome            = item.Nome;
                    existente.Tipo            = item.Tipo;
                    existente.Duracao         = item.Duracao;
                    existente.OrigemSaida     = item.OrigemSaida;
                    existente.RamalOrigem     = item.RamalOrigem;
                    existente.RamalDestino    = item.RamalDestino;
                    existente.RamalAtendeu    = item.RamalAtendeu;
                    existente.GravacaoArquivo = item.GravacaoArquivo;
                    existente.GravacaoUrl     = item.GravacaoUrl;
                }
            }

            // Remove locally-tracked items superseded by a CDR record (same external number, ±2 min).
            // Pré-normaliza os números do CDR em Dictionary<numero, List<DateTime>> para lookup O(1),
            // evitando O(n*m) do RemoveAll().Any() original.
            var cdrPorNumero = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
            var cdrRamalPorNumero = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in itensCdr)
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
                var numE = PhoneNumberNormalizer.NormalizeBrazilPhone(DialPlanService.RemoverPrefixoDeRota(e.Numero));
                if (DialPlanService.EhRamalInterno(numE))
                {
                    if (!cdrRamalPorNumero.TryGetValue(numE, out var datasR)) return false;
                    return datasR.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= 2);
                }
                if (numE.Length < 8) return false;
                if (!cdrPorNumero.TryGetValue(numE, out var datas)) return false;
                return datas.Any(d => Math.Abs((d - e.DataHora).TotalMinutes) <= 2);
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

            Salvar(semDuplicatas.OrderByDescending(i => i.DataHora).Take(5000).ToList());

            return novos;
        }
    }
}
