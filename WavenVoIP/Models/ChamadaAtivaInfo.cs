using System;

namespace WavenVoIP.Models
{
    // v2.5.0 — "Puxar ligação": situação de uma chamada de cliente que está viva no PABX agora.
    public enum StatusChamadaAtiva
    {
        EmAtendimento,   // em conversa com um ramal
        TocandoRamais,   // tocando em ramal/grupo, ninguém atendeu ainda
        NaFila,          // aguardando na fila de atendimento
        NaUra,           // ouvindo/navegando no menu da URA
        ForaDoHorario,   // ouvindo a mensagem de fora do horário
        Mensagem,        // ouvindo outro anúncio do PABX
        CaixaPostal      // na caixa postal
    }

    // Linha crua do "CoreShowChannels" do AMI (só os campos usados).
    public sealed record CanalAtivo(
        string Channel, string Linkedid, string Uniqueid, string BridgeId,
        string CallerIDNum, string CallerIDName, string ConnectedLineNum,
        string Context, string Exten, string Application, string ApplicationData,
        int DuracaoSeg);

    // v2.5.0 — quem está na ligação em conferência deste ramal (depois de "Puxar ligação").
    // Tipo: "cliente", "atendente" (outro ramal) ou "convidado" (número chamado para a sala).
    public sealed record ParticipanteLigacao(string Tipo, string Identificador, string Nome, string Canal);

    public sealed class ChamadaAtivaInfo
    {
        public string CanalCliente { get; init; } = string.Empty;   // canal real do cliente no Asterisk
        public string Linkedid { get; init; } = string.Empty;
        public string Uniqueid { get; init; } = string.Empty;
        public string Numero { get; init; } = string.Empty;
        public string Nome { get; init; } = string.Empty;
        public string Tronco { get; init; } = string.Empty;         // "WhatsApp TIM", "Operadora", "0800"…
        public StatusChamadaAtiva Status { get; init; }
        public string RamalAtendente { get; init; } = string.Empty; // vazio quando ninguém atendeu
        public string Fila { get; init; } = string.Empty;
        public int DuracaoSeg { get; init; }
        public DateTime LidoEm { get; init; } = DateTime.Now;

        public string StatusTexto => Status switch
        {
            StatusChamadaAtiva.EmAtendimento => "Em atendimento",
            StatusChamadaAtiva.TocandoRamais => "Tocando nos ramais",
            StatusChamadaAtiva.NaFila        => string.IsNullOrWhiteSpace(Fila) ? "Na fila" : $"Na fila {Fila}",
            StatusChamadaAtiva.NaUra         => "Na URA",
            StatusChamadaAtiva.ForaDoHorario => "Fora do horário",
            StatusChamadaAtiva.Mensagem      => "Ouvindo mensagem",
            StatusChamadaAtiva.CaixaPostal   => "Na caixa postal",
            _ => Status.ToString()
        };
    }
}
