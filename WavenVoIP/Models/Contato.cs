using System;
using System.Text.Json.Serialization;

namespace WavenVoIP.Models
{
    public class Contato
    {
        public string Nome { get; set; } = string.Empty;
        public string Numero { get; set; } = string.Empty;
        public string Observacao { get; set; } = string.Empty;
        public bool EhRamalIssabel { get; set; } = false;
        public bool FonteGoogle { get; set; } = false;
        public DateTime? AtualizadoEm { get; set; }

        // ID do contato na Waven API — null quando não sincronizado
        public string? WavenApiId { get; set; }

        // ID único do contato no Google Contacts (person.ResourceName)
        // Preservado ao converter Google → Empresa para evitar duplicatas na re-sync
        public string? GoogleContactId { get; set; }

        // v2.5.0 — alteração feita aqui que ainda não chegou a outra origem:
        // "" = tudo sincronizado; "google" = nome ainda não enviado ao Google;
        // "google_sem_permissao" = o WavenVoIP só tem leitura no Google; "api" = aguardando a Waven API.
        public string SincronizacaoPendente { get; set; } = string.Empty;

        [JsonIgnore]
        public bool TemPendencia => !string.IsNullOrEmpty(SincronizacaoPendente);

        // Origem do contato, por extenso (segunda linha da lista de contatos).
        [JsonIgnore]
        public string OrigemTexto =>
            EhRamalIssabel  ? "Ramal do PABX" :
            FonteGoogle     ? "Google Contacts" :
            EhCompartilhado ? (string.IsNullOrWhiteSpace(GoogleContactId) ? "Empresa (Waven API)" : "Empresa (Waven API) · também no Google") :
                              (string.IsNullOrWhiteSpace(GoogleContactId) ? "Somente neste computador" : "Editado aqui · também no Google");

        [JsonIgnore]
        public string LinhaOrigem
        {
            get
            {
                var obs = (Observacao ?? string.Empty).Trim();
                var s = OrigemTexto;
                if (obs.Length > 0 && obs != "Google Contacts" && obs != "Contato empresa" && !EhRamalIssabel) s += " · " + obs;
                return SincronizacaoPendente switch
                {
                    "google"               => s + " · alteração ainda não enviada ao Google",
                    "google_sem_permissao" => s + " · não enviado ao Google (sem permissão de escrita)",
                    "api"                  => s + " · aguardando envio à Waven API",
                    _                      => s
                };
            }
        }

        // Transient — set at display time, never persisted
        [JsonIgnore]
        public bool EhFavorito { get; set; }

        // True quando sincronizado com a Waven API (visivel para todos os ramais)
        [JsonIgnore]
        public bool EhCompartilhado =>
            !EhRamalIssabel && !FonteGoogle && !string.IsNullOrWhiteSpace(WavenApiId);

        // Badge: 🌐 Google | 🏢 Empresa | 💾 Local | vazio para ramais
        [JsonIgnore]
        public string BadgeText =>
            EhRamalIssabel   ? string.Empty :
            FonteGoogle      ? "\U0001F310" :   // 🌐
            EhCompartilhado  ? "\U0001F3E2" :   // 🏢
                               "\U0001F4BE";    // 💾

        [JsonIgnore]
        public string BadgeTooltip =>
            EhRamalIssabel   ? string.Empty :
            FonteGoogle      ? "Google Contacts" :
            EhCompartilhado  ? "Contato compartilhado da empresa" :
                               "Contato local";

        [JsonIgnore]
        public bool MostrarBadge => !EhRamalIssabel;

        // Transient — marcado durante janela de undo; nunca persistido
        [JsonIgnore]
        public bool PendingDelete { get; set; }
    }
}
