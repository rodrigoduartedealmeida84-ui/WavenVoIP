using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using WavenVoIP.Models;
using WavenVoIP;

namespace WavenVoIP.Services
{
    public static class CanalIdentificacaoService
    {
        private static readonly (string Chave, string Nome)[] RotasEntrada = new[]
        {
            ("6631998716", "Operadora"),
            ("08001901900", "0800"),
            ("556684263277", "WhatsApp TIM"),
            ("556696308630", "WhatsApp Vivo"),
            ("IN-BRDID-6631998716", "Operadora"),
            ("VONO-0800-ENTRADA", "0800"),
            ("WAVOIP-556684263277", "WhatsApp TIM"),
            ("WAVOIP-556696308630", "WhatsApp Vivo")
        };

        private static SipConfig? _configCache;
        private static DateTime _configCacheExpiry = DateTime.MinValue;

        private static SipConfig? ObterConfigCache()
        {
            if (DateTime.Now <= _configCacheExpiry && _configCache != null) return _configCache;
            try { _configCache = SipConfig.CarregarSalva(); } catch { _configCache = null; }
            _configCacheExpiry = DateTime.Now.AddSeconds(30);
            return _configCache;
        }

        private static IEnumerable<(string Chave, string Nome)> ObterRotasConfiguradas()
        {
            foreach (var rota in RotasEntrada) yield return rota;

            var cfg = ObterConfigCache();
            if (cfg == null) yield break;

            foreach (var chave in SepararChaves(cfg.CanalOperadora)) yield return (chave, "Operadora");
            foreach (var chave in SepararChaves(cfg.Canal0800)) yield return (chave, "0800");
            foreach (var chave in SepararChaves(cfg.CanalWhatsAppTim)) yield return (chave, "WhatsApp TIM");
            foreach (var chave in SepararChaves(cfg.CanalWhatsAppVivo)) yield return (chave, "WhatsApp Vivo");
        }

        private static IEnumerable<string> SepararChaves(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) yield break;
            foreach (var p in valor.Split(new[] { ';', ',', '|', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = p.Trim();
                if (!string.IsNullOrWhiteSpace(v)) yield return v;
            }
        }


        public static string IdentificarEntrada(string textoSip, params string[] candidatos)
        {
            foreach (var candidato in candidatos ?? Array.Empty<string>())
            {
                var nome = IdentificarPorValor(candidato);
                if (!string.IsNullOrWhiteSpace(nome)) return nome;
            }

            var texto = textoSip ?? string.Empty;
            foreach (var rota in ObterRotasConfiguradas())
            {
                if (texto.IndexOf(rota.Chave, StringComparison.OrdinalIgnoreCase) >= 0)
                    return rota.Nome;
            }

            // Fallback para DIDs numéricos encontrados no SIP bruto.
            foreach (Match m in Regex.Matches(texto, @"\d{8,14}"))
            {
                var nome = IdentificarPorValor(m.Value);
                if (!string.IsNullOrWhiteSpace(nome)) return nome;
            }

            return "Entrada não identificada";
        }

        public static string IdentificarPorValor(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
            var normalizado = DialPlanService.RemoverDuplicacaoSequencial(valor);

            foreach (var rota in ObterRotasConfiguradas())
            {
                if (normalizado.IndexOf(rota.Chave, StringComparison.OrdinalIgnoreCase) >= 0)
                    return rota.Nome;
            }

            return string.Empty;
        }

        // Labels that should never appear raw in the UI — replace with fallback
        private static readonly System.Collections.Generic.HashSet<string> _labelsRawProibidas =
            new(System.StringComparer.OrdinalIgnoreCase)
            { "SIP", "DAHDI", "Local", "PJSIP", "Macro" };

        public static string NormalizarCanal(string origemSaida, TipoHistoricoLigacao tipo, string numero)
        {
            var canal = origemSaida ?? string.Empty;

            // Queue/URA channel: map to human-readable label based on call outcome
            if (string.Equals(canal.Trim(), "Queue", StringComparison.OrdinalIgnoreCase))
            {
                if (tipo == TipoHistoricoLigacao.Perdida)               return "Chamada abandonada";
                if (tipo == TipoHistoricoLigacao.NaoAtendidaNesseRamal) return "Desligou antes da fila";
                return "URA";
            }

            // Strip raw technical labels that should never appear in the UI
            if (_labelsRawProibidas.Contains(canal.Trim()))
                canal = string.Empty;

            var porValor = IdentificarPorValor(canal);
            if (!string.IsNullOrWhiteSpace(porValor)) return porValor;

            // Corrige históricos antigos que foram marcados errado como WhatsApp TIM.
            var numeroCanal = IdentificarPorValor(numero ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(numeroCanal)) return numeroCanal;

            if (!string.IsNullOrWhiteSpace(canal)) return canal;
            if (tipo == TipoHistoricoLigacao.Realizada) return "Saída não identificada";

            // Fallback prático: quando o Issabel não encaminha DID/DDR no INVITE,
            // a entrada fica sem identificação. Para o ambiente atual, entradas externas
            // sem DID conhecido são marcadas como Operadora, enquanto ramais internos
            // continuam identificados como internos.
            var n = DialPlanService.RemoverDuplicacaoSequencial(numero ?? string.Empty);
            if (DialPlanService.EhRamalInterno(n)) return "Ramal interno";
            if (!string.IsNullOrWhiteSpace(n)) return "Operadora";
            return "Entrada não identificada";
        }

        public static Brush BrushCanal(string canal)
        {
            canal = canal ?? string.Empty;
            if (canal.IndexOf("WhatsApp", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SolidColorBrush(Color.FromRgb(22, 163, 74));
            if (canal.IndexOf("0800", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SolidColorBrush(Color.FromRgb(2, 132, 199));
            if (canal.IndexOf("Operadora", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SolidColorBrush(Color.FromRgb(124, 58, 237));
            if (canal.IndexOf("URA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canal.IndexOf("abandonada", StringComparison.OrdinalIgnoreCase) >= 0 ||
                canal.IndexOf("fila", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SolidColorBrush(Color.FromRgb(217, 119, 6));
            return new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }
    }
}
