using System;

namespace WavenVoIP.Models
{
    public class FavoriteItem
    {
        public string Nome { get; set; } = string.Empty;
        public string Numero { get; set; } = string.Empty;
        public string Ramal { get; set; } = string.Empty;
        public bool Favorito { get; set; } = true;
        public int Ordem { get; set; }
        public int UsageCount { get; set; }

        // Vínculo estável ao contato (Contato.WavenApiId). Null quando o contato
        // ainda não foi sincronizado com a Waven API — nesse caso o vínculo é
        // resolvido por telefone normalizado (fallback), ver FavoritesStorageService.
        public string? ContactId { get; set; }

        // v2.4.1 — favorito órfão: sync com a API não conseguiu confirmar que este
        // contato continua favoritado (ID ainda não pushado, contato temporariamente
        // não resolvido, etc). Marcado como órfão em vez de apagado; nunca é removido
        // automaticamente, só por ação explícita do usuário. Ver AtualizarFavoritosLocais.
        public bool Orfao { get; set; }
        public DateTime? OrfaoDesde { get; set; }

        // v2.4.1 — true assim que a API confirmou (favoritosAtuais) que este contato
        // está favoritado pelo menos uma vez. Antes disso, ausência em favoritosAtuais
        // NUNCA é interpretada como remoção (pode ser push ainda em voo/offline). Depois
        // de confirmado, uma ausência sustentada por 2 ciclos consecutivos de sync É aceita
        // como remoção legítima (feita em outro computador/sessão do mesmo ramal, ou
        // favorito global removido por outro usuário).
        public bool ConfirmadoPelaApi { get; set; }
    }
}
