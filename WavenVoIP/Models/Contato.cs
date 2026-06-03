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
