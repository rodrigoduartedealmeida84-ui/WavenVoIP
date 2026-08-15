using Microsoft.EntityFrameworkCore;
using Serilog;
using WavenApi;
using WavenApi.Data;
using WavenApi.Endpoints;
using WavenApi.Middleware;
using WavenApi.Services;

// Serilog mínimo antes de ler config (captura erros de startup)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration));

    // ── Options ───────────────────────────────────────────────────────────────
    builder.Services.Configure<WavenApiOptions>(
        builder.Configuration.GetSection("WavenApi"));

    // ── DB ────────────────────────────────────────────────────────────────────
    var dbPath = builder.Configuration["WavenApi:DbPath"]
                 ?? "/opt/waven-api/data/waven.db";
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    builder.Services.AddDbContext<WavenDbContext>(opt =>
        opt.UseSqlite($"Data Source={dbPath}"));

    builder.Services.AddHostedService<DiagnosticRetentionService>();

    // ── Limite de payload ─────────────────────────────────────────────────────
    var maxBytes = builder.Configuration.GetValue<int>("WavenApi:MaxPayloadBytes", 1_048_576);
    builder.WebHost.ConfigureKestrel(k =>
        k.Limits.MaxRequestBodySize = maxBytes);

    var app = builder.Build();

    // ── Criar / migrar banco na subida ────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<WavenDbContext>();
        db.Database.EnsureCreated();

        // v2.4.2 — EnsureCreated() só cria o schema inteiro quando o arquivo do banco
        // NÃO existe; em produção o waven.db já existe (Contacts/Favoritos/CompanyConfig),
        // então as tabelas novas (Diagnostic*) nunca seriam criadas sozinhas — a primeira
        // chamada a /api/diagnostics/heartbeat quebraria com "no such table". Este passo é
        // idempotente e não toca nas tabelas existentes: gera o script de criação do
        // modelo INTEIRO e adiciona "IF NOT EXISTS" em cada CREATE TABLE/INDEX, então só
        // as tabelas realmente ausentes são criadas — dado real de Contacts/Favoritos
        // nunca é afetado.
        var createScript = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"")
            .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"")
            .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"");
        db.Database.ExecuteSqlRaw(createScript);
    }

    // ── Garante JSON em qualquer excecao nao-tratada ──────────────────────────
    app.UseExceptionHandler(ex =>
        ex.Run(async ctx =>
        {
            ctx.Response.StatusCode  = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
                { ok = false, error = "Erro interno do servidor", status = 500 });
        }));

    // ── Garante JSON em rotas nao encontradas (404) e outros status-codes ─────
    app.UseStatusCodePages(async ctx =>
    {
        var r = ctx.HttpContext.Response;
        if (!r.HasStarted && !r.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            r.ContentType = "application/json";
            await r.WriteAsJsonAsync(new
            {
                ok     = false,
                error  = $"Recurso nao encontrado: {ctx.HttpContext.Request.Path}",
                status = r.StatusCode
            });
        }
    });

    // ── Painel administrativo estático (wwwroot/diagnostics) ───────────────────
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // ── Auth middleware (antes das rotas) ─────────────────────────────────────
    app.UseMiddleware<BearerAuthMiddleware>();

    // ── Endpoints ─────────────────────────────────────────────────────────────
    app.MapHealthEndpoints();
    app.MapContactEndpoints();
    app.MapCompanyConfigEndpoints();
    app.MapCdrEndpoints();
    app.MapAmiEndpoints();
    app.MapDiagnosticsEndpoints();

    Log.Information("WavenApi iniciada em {Urls}", string.Join(", ",
        builder.Configuration["ASPNETCORE_URLS"] ?? "http://127.0.0.1:5005"));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WavenApi encerrada inesperadamente");
}
finally
{
    Log.CloseAndFlush();
}
