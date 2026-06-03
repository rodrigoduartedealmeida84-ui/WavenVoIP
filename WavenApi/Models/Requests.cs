namespace WavenApi.Models;

public record CreateContactRequest(
    string Nome,
    string Numero,
    string? Empresa,
    string? Observacao,
    string? CriadoPor
);

public record UpdateContactRequest(
    string Nome,
    string? Empresa,
    string? Observacao,
    string? AtualizadoPor,
    DateTime? AtualizadoEm
);

public record DeleteContactRequest(string? ExcluidoPor);

public record FavoriteRequest(
    string? Ramal,
    string? TipoFavorito,
    string? CriadoPor
);

public record RemoveFavoriteRequest(
    string? Ramal,
    string? TipoFavorito,
    string? AtualizadoPor
);

public record UpdateCompanyConfigRequest(string? ConfigJson, string? AtualizadoPor);
