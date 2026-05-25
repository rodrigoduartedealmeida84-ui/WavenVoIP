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
        CaixaPostal = 4
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
                        return Nome;

                    return numLimpo;
                }
                catch { return string.IsNullOrWhiteSpace(Nome) ? Numero : Nome; }
            }
        }

        public string OrigemSaida { get; set; } = "";

        public string NumeroLimpoVisual => LimparPrefixoRota(Numero);

        public string OrigemSaidaVisual
        {
            get
            {
                var canal = CanalIdentificacaoService.NormalizarCanal(OrigemSaida, Tipo, Numero);
                if (canal == "Saída não identificada" && Tipo == TipoHistoricoLigacao.Realizada)
                    return DetectarSaidaPeloPrefixo(Numero);
                return canal;
            }
        }

        [JsonIgnore]
        public Brush CorCanal => CanalIdentificacaoService.BrushCanal(OrigemSaidaVisual);

        public string Icone => Tipo switch
        {
            TipoHistoricoLigacao.Realizada => "↗",
            TipoHistoricoLigacao.Recebida => "↙",
            TipoHistoricoLigacao.Perdida => "↘",
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => "↩",
            TipoHistoricoLigacao.CaixaPostal => "✉",
            _ => "•"
        };

        public string TipoTexto => Tipo switch
        {
            TipoHistoricoLigacao.Realizada => "Realizada",
            TipoHistoricoLigacao.Recebida => "Recebida",
            TipoHistoricoLigacao.Perdida => "Perdida",
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => "Não atendida",
            TipoHistoricoLigacao.CaixaPostal => "Caixa postal",
            _ => Tipo.ToString()
        };

        [JsonIgnore]
        public Brush CorTipo => Tipo switch
        {
            TipoHistoricoLigacao.Realizada => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            TipoHistoricoLigacao.Recebida => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            TipoHistoricoLigacao.Perdida => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => new SolidColorBrush(Color.FromRgb(234, 88, 12)),
            TipoHistoricoLigacao.CaixaPostal => new SolidColorBrush(Color.FromRgb(161, 98, 7)),
            _ => Brushes.Gray
        };

        [JsonIgnore]
        public Brush CorTipoFundo => Tipo switch
        {
            TipoHistoricoLigacao.Realizada => new SolidColorBrush(Color.FromRgb(240, 253, 244)),
            TipoHistoricoLigacao.Recebida => new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            TipoHistoricoLigacao.Perdida => new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            TipoHistoricoLigacao.NaoAtendidaNesseRamal => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
            TipoHistoricoLigacao.CaixaPostal => new SolidColorBrush(Color.FromRgb(254, 249, 195)),
            _ => new SolidColorBrush(Color.FromRgb(241, 245, 249))
        };

        [JsonIgnore]
        public string RamalExibido
        {
            get
            {
                if (Tipo == TipoHistoricoLigacao.Realizada)
                {
                    if (!string.IsNullOrWhiteSpace(RamalOrigem) && EhRamalString(RamalOrigem))
                        return NomeOuRamal(RamalOrigem);
                    return string.Empty;
                }
                if (Tipo == TipoHistoricoLigacao.CaixaPostal)
                    return string.Empty; // TipoTexto already says "Caixa postal"
                // Recebida / Perdida / NaoAtendidaNesseRamal: show who answered (or dest ramal)
                if (!string.IsNullOrWhiteSpace(RamalAtendeu))
                    return NomeOuRamal(RamalAtendeu);
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
            return n[0] switch
            {
                '1' => "Operadora",
                '2' => "WhatsApp TIM",
                '3' => "WhatsApp Vivo",
                _ => "Ramal interno / sem rota"
            };
        }
    }
}
