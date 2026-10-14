using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WavenVoIP.Models;

namespace WavenVoIP.Services
{
    // v2.5.0 — contador do botão "Não atendidas" como "mensagens não lidas".
    // Abrir a área Não atendidas marca as chamadas que estão ali como VISUALIZADAS: o contador
    // zera e só volta a subir com chamadas não atendidas novas. Nada é apagado: o registro continua
    // no Histórico (e no historico.json) e as regras de aviso/pendência não mudam — este arquivo só
    // guarda quais ligações o usuário já viu (naoatendidas_vistas.json, em %LOCALAPPDATA%\WavenVoIP).
    public static class NaoAtendidasVistasService
    {
        private const int DiasGuardados = 120;

        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WavenVoIP",
            "naoatendidas_vistas.json");

        private static readonly object _lock = new object();
        private static Dictionary<string, DateTime>? _vistas; // chave da ligação -> horário da ligação

        // Uma ligação = uma chave. LinkedId primeiro: o UniqueId do registro pode mudar conforme as
        // linhas do CDR chegam (ver MesclarCdr). Sem os dois, número + horário ao segundo.
        public static string Chave(HistoricoLigacaoItem i)
        {
            if (!string.IsNullOrWhiteSpace(i.LinkedId)) return "L:" + i.LinkedId.Trim();
            if (!string.IsNullOrWhiteSpace(i.UniqueId)) return "U:" + i.UniqueId.Trim();
            var num = new string((i.Numero ?? string.Empty).Where(char.IsDigit).ToArray());
            return $"N:{num}|{i.DataHora:yyyyMMddHHmmss}";
        }

        // Quantas ligações não atendidas ainda não foram visualizadas (cada ligação conta uma vez,
        // mesmo que apareça repetida na lista).
        public static int ContarNaoVistas(IEnumerable<HistoricoLigacaoItem> itens, IReadOnlySet<string> vistas) =>
            itens.Where(NaoAtendidasPolicy.EhNaoAtendida).Select(Chave).Where(k => !vistas.Contains(k)).Distinct().Count();

        public static IReadOnlySet<string> Instantaneo()
        {
            lock (_lock) { return new HashSet<string>(Carregar().Keys); }
        }

        public static int ContarNaoVistas(IEnumerable<HistoricoLigacaoItem> itens) => ContarNaoVistas(itens, Instantaneo());

        // Marca como visualizadas todas as não atendidas da lista. Devolve quantas eram novas.
        public static int MarcarVistas(IEnumerable<HistoricoLigacaoItem> itens)
        {
            lock (_lock)
            {
                var vistas = Carregar();
                int novas = 0;
                foreach (var i in itens.Where(NaoAtendidasPolicy.EhNaoAtendida))
                {
                    var k = Chave(i);
                    if (vistas.ContainsKey(k)) continue;
                    vistas[k] = i.DataHora == default ? DateTime.Now : i.DataHora;
                    novas++;
                }
                if (novas > 0) Salvar(vistas);
                return novas;
            }
        }

        private static Dictionary<string, DateTime> Carregar()
        {
            if (_vistas != null) return _vistas;
            try
            {
                _vistas = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)) ?? new()
                    : new();
            }
            catch { _vistas = new(); }
            return _vistas;
        }

        private static void Salvar(Dictionary<string, DateTime> vistas)
        {
            try
            {
                var limite = DateTime.Now.AddDays(-DiasGuardados);
                foreach (var k in vistas.Where(p => p.Value < limite).Select(p => p.Key).ToList()) vistas.Remove(k);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(vistas));
                File.Move(tmp, _path, true);
            }
            catch { }
        }
    }
}
