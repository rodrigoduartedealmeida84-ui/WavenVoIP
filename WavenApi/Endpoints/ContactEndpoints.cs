using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WavenApi.Data;
using WavenApi.Models;
using WavenApi.Services;

namespace WavenApi.Endpoints;

public static class ContactEndpoints
{
    public static void MapContactEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/contacts");
        g.MapGet("",            GetContacts);
        g.MapGet("/sync",       SyncContacts);
        g.MapPost("",           CreateContact);
        g.MapPut("/{id}",       UpdateContact);
        g.MapDelete("/{id}",    DeleteContact);
        g.MapPost("/{id}/favorite",   AddFavorite);
        g.MapDelete("/{id}/favorite", RemoveFavorite);
    }

    // ── GET /api/contacts?ramal=100 ───────────────────────────────────────────

    private static async Task<IResult> GetContacts(string ramal, WavenDbContext db)
    {
        var contacts = await db.Contacts
            .Where(c => !c.Excluido)
            .Include(c => c.Favoritos)
            .OrderBy(c => c.Nome)
            .ToListAsync();

        return Results.Ok(contacts.Select(c => ToResponse(c, ramal)));
    }

    // ── GET /api/contacts/sync?since=<ISO8601>&ramal=100 ─────────────────────

    private static async Task<IResult> SyncContacts(string ramal, string? since, WavenDbContext db)
    {
        var sinceDate = DateTime.MinValue;
        if (!string.IsNullOrWhiteSpace(since))
            DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out sinceDate);

        var contacts = await db.Contacts
            .Where(c => c.AtualizadoEm > sinceDate)
            .Include(c => c.Favoritos)
            .OrderBy(c => c.AtualizadoEm)
            .ToListAsync();

        var favoritosAtuais = await db.Favoritos
            .Where(f => (f.Ramal == ramal || f.TipoFavorito == "global")
                     && db.Contacts.Any(c => c.Id == f.ContactId && !c.Excluido))
            .Select(f => f.ContactId)
            .Distinct()
            .ToListAsync();

        return Results.Ok(new
        {
            serverTime      = DateTime.UtcNow,
            contacts        = contacts.Select(c => ToResponse(c, ramal)),
            favoritosAtuais
        });
    }

    // ── POST /api/contacts ────────────────────────────────────────────────────

    private static async Task<IResult> CreateContact(
        CreateContactRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Contacts");

        if (string.IsNullOrWhiteSpace(req.Nome) || string.IsNullOrWhiteSpace(req.Numero))
            return Results.BadRequest(new { error = "Nome e Numero são obrigatórios" });

        var numNorm = PhoneNormalizer.Normalize(req.Numero);

        var existing = await db.Contacts
            .Include(c => c.Favoritos)
            .FirstOrDefaultAsync(c => c.NumeroNormalizado == numNorm);

        Contact contact;

        if (existing is { Excluido: false })
        {
            logger.LogInformation("CONTACT_DUPLICATE | numero={Numero}", numNorm);
            return Results.Conflict(new
            {
                message = "Contato já existe",
                contact = ToResponse(existing, req.CriadoPor ?? string.Empty)
            });
        }

        if (existing is { Excluido: true })
        {
            existing.Nome         = req.Nome;
            existing.Empresa      = req.Empresa;
            existing.Observacao   = req.Observacao;
            existing.Excluido     = false;
            existing.ExcluidoEm   = null;
            existing.AtualizadoEm  = DateTime.UtcNow;
            existing.AtualizadoPor = req.CriadoPor ?? string.Empty;
            contact = existing;
            logger.LogInformation("CONTACT_REACTIVATED | id={Id} nome={Nome}", contact.Id, contact.Nome);
        }
        else
        {
            contact = new Contact
            {
                Id                = Guid.NewGuid().ToString(),
                Nome              = req.Nome,
                Numero            = req.Numero,
                NumeroNormalizado = numNorm,
                Empresa           = req.Empresa,
                Observacao        = req.Observacao,
                CriadoPor         = req.CriadoPor ?? string.Empty,
                AtualizadoPor     = req.CriadoPor ?? string.Empty,
                CriadoEm         = DateTime.UtcNow,
                AtualizadoEm     = DateTime.UtcNow
            };
            db.Contacts.Add(contact);
            logger.LogInformation("CONTACT_CREATED | id={Id} nome={Nome} numero={Numero}",
                contact.Id, contact.Nome, numNorm);
        }

        await db.SaveChangesAsync();
        return Results.Created($"/api/contacts/{contact.Id}",
            ToResponse(contact, req.CriadoPor ?? string.Empty));
    }

    // ── PUT /api/contacts/{id} ────────────────────────────────────────────────

    private static async Task<IResult> UpdateContact(
        string id, UpdateContactRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Contacts");

        var contact = await db.Contacts
            .Include(c => c.Favoritos)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (contact is null || contact.Excluido) return Results.NotFound();

        if (req.AtualizadoEm.HasValue && req.AtualizadoEm.Value < contact.AtualizadoEm)
        {
            logger.LogInformation("CONTACT_CONFLICT | id={Id} server_wins", id);
            return Results.Conflict(new
            {
                message = "Conflito: versão do servidor é mais recente",
                contact = ToResponse(contact, req.AtualizadoPor ?? string.Empty)
            });
        }

        if (string.IsNullOrWhiteSpace(req.Nome))
            return Results.BadRequest(new { error = "Nome é obrigatório" });

        contact.Nome         = req.Nome;
        contact.Empresa      = req.Empresa;
        contact.Observacao   = req.Observacao;
        contact.AtualizadoPor = req.AtualizadoPor ?? string.Empty;
        contact.AtualizadoEm  = DateTime.UtcNow;

        await db.SaveChangesAsync();
        logger.LogInformation("CONTACT_UPDATED | id={Id} nome={Nome}", id, contact.Nome);
        return Results.Ok(ToResponse(contact, req.AtualizadoPor ?? string.Empty));
    }

    // ── DELETE /api/contacts/{id} ─────────────────────────────────────────────

    private static async Task<IResult> DeleteContact(
        string id, [FromBody] DeleteContactRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Contacts");
        var contact = await db.Contacts.FindAsync(id);
        if (contact is null || contact.Excluido) return Results.NotFound();

        contact.Excluido      = true;
        contact.ExcluidoEm    = DateTime.UtcNow;
        contact.AtualizadoEm  = DateTime.UtcNow;
        contact.AtualizadoPor = req.ExcluidoPor ?? string.Empty;

        await db.SaveChangesAsync();
        logger.LogInformation("CONTACT_DELETED | id={Id} nome={Nome}", id, contact.Nome);
        return Results.Ok(new { message = "Contato excluído" });
    }

    // ── POST /api/contacts/{id}/favorite ─────────────────────────────────────

    private static async Task<IResult> AddFavorite(
        string id, FavoriteRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Contacts");
        var contact = await db.Contacts.FindAsync(id);
        if (contact is null || contact.Excluido) return Results.NotFound();

        var tipo  = string.Equals(req.TipoFavorito, "global", StringComparison.OrdinalIgnoreCase)
                    ? "global" : "usuario";
        var ramal = tipo == "global" ? null : req.Ramal;

        if (tipo == "usuario" && string.IsNullOrWhiteSpace(ramal))
            return Results.BadRequest(new { error = "Ramal obrigatório para favorito tipo 'usuario'" });

        var exists = await db.Favoritos.AnyAsync(f =>
            f.ContactId == id && f.Ramal == ramal && f.TipoFavorito == tipo);

        if (exists) return Results.Ok(new { message = "Já é favorito" });

        db.Favoritos.Add(new Favorito
        {
            Id           = Guid.NewGuid().ToString(),
            ContactId    = id,
            Ramal        = ramal,
            TipoFavorito = tipo,
            CriadoEm    = DateTime.UtcNow,
            CriadoPor    = req.CriadoPor ?? ramal ?? "system"
        });

        contact.AtualizadoEm  = DateTime.UtcNow;
        contact.AtualizadoPor = req.CriadoPor ?? ramal ?? string.Empty;

        await db.SaveChangesAsync();
        logger.LogInformation("FAVORITE_ADDED | contactId={Id} ramal={Ramal} tipo={Tipo}",
            id, ramal ?? "global", tipo);
        return Results.Created($"/api/contacts/{id}/favorite", new { message = "Favorito adicionado" });
    }

    // ── DELETE /api/contacts/{id}/favorite ───────────────────────────────────

    private static async Task<IResult> RemoveFavorite(
        string id, [FromBody] RemoveFavoriteRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.Contacts");
        var tipo  = string.Equals(req.TipoFavorito, "global", StringComparison.OrdinalIgnoreCase)
                    ? "global" : "usuario";
        var ramal = tipo == "global" ? null : req.Ramal;

        var fav = await db.Favoritos.FirstOrDefaultAsync(f =>
            f.ContactId == id && f.Ramal == ramal && f.TipoFavorito == tipo);

        if (fav is null) return Results.NotFound();

        db.Favoritos.Remove(fav);

        var contact = await db.Contacts.FindAsync(id);
        if (contact is not null)
        {
            contact.AtualizadoEm  = DateTime.UtcNow;
            contact.AtualizadoPor = req.AtualizadoPor ?? ramal ?? string.Empty;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("FAVORITE_REMOVED | contactId={Id} ramal={Ramal} tipo={Tipo}",
            id, ramal ?? "global", tipo);
        return Results.Ok(new { message = "Favorito removido" });
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static object ToResponse(Contact c, string ramal) => new
    {
        c.Id,
        c.Nome,
        c.Numero,
        c.NumeroNormalizado,
        c.Empresa,
        c.Observacao,
        isFavorito   = c.Favoritos.Any(f => f.Ramal == ramal || f.TipoFavorito == "global"),
        tipoFavorito = c.Favoritos
            .Where(f => f.Ramal == ramal || f.TipoFavorito == "global")
            .Select(f => f.TipoFavorito)
            .FirstOrDefault(),
        c.CriadoPor,
        c.AtualizadoPor,
        c.CriadoEm,
        c.AtualizadoEm,
        c.Excluido,
        c.ExcluidoEm
    };
}
