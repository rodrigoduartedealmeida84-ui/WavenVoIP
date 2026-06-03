using Microsoft.EntityFrameworkCore;
using WavenApi.Data;
using WavenApi.Models;

namespace WavenApi.Endpoints;

public static class CompanyConfigEndpoints
{
    public static void MapCompanyConfigEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/company");
        g.MapGet("/config",  GetConfig);
        g.MapPut("/config",  UpdateConfig);
    }

    private static async Task<IResult> GetConfig(WavenDbContext db)
    {
        var cfg = await db.CompanyConfig.FindAsync(1);
        return Results.Ok(new
        {
            configJson   = cfg?.ConfigJson ?? "{}",
            atualizadoEm = cfg?.AtualizadoEm ?? DateTime.UtcNow
        });
    }

    private static async Task<IResult> UpdateConfig(
        UpdateCompanyConfigRequest req, WavenDbContext db, ILoggerFactory lf)
    {
        var logger = lf.CreateLogger("WavenApi.CompanyConfig");
        var cfg = await db.CompanyConfig.FindAsync(1);
        if (cfg is null)
        {
            cfg = new CompanyConfigEntry { Id = 1 };
            db.CompanyConfig.Add(cfg);
        }
        cfg.ConfigJson    = req.ConfigJson ?? "{}";
        cfg.AtualizadoEm  = DateTime.UtcNow;
        cfg.AtualizadoPor = req.AtualizadoPor ?? string.Empty;
        await db.SaveChangesAsync();
        logger.LogInformation("COMPANY_CONFIG_UPDATED | por={Por}", req.AtualizadoPor);
        return Results.Ok(new { message = "Configuração atualizada", atualizadoEm = cfg.AtualizadoEm });
    }
}
