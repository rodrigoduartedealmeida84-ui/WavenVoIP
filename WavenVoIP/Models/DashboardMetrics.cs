using System.Collections.Generic;

namespace WavenVoIP.Models
{
    public class DashboardMetrics
    {
        public int Recebidas { get; set; }
        public int Realizadas { get; set; }
        public int Perdidas { get; set; }
        public int TotalAtendimentos { get; set; }
        public string TempoMedio { get; set; } = "—";
        public int Gravacoes { get; set; }
        public List<HourBarItem> ChamadasPorHora { get; set; } = new();
        public List<RamalMetrica> PorRamal { get; set; } = new();
    }

    public class HourBarItem
    {
        public string HoraLabel { get; set; } = "";
        public int Contagem { get; set; }
        public double AlturaBar { get; set; }
        public double EspacadorAltura { get; set; }
        public string Tooltip { get; set; } = "";
        public string ContagemLabel => Contagem > 0 ? Contagem.ToString() : "";
        public bool TemDados => Contagem > 0;
    }

    public class RamalMetrica
    {
        public string Ramal { get; set; } = "";
        public string Nome { get; set; } = "";
        public int Recebidas { get; set; }
        public int Realizadas { get; set; }
        public int Perdidas { get; set; }
        public string TempoMedio { get; set; } = "—";
        public int Total => Recebidas + Realizadas;
    }
}
