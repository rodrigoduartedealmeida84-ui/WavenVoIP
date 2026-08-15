using Microsoft.Extensions.Options;

namespace WavenApi.Middleware;

public class BearerAuthMiddleware(RequestDelegate next, IOptions<WavenApiOptions> options, ILogger<BearerAuthMiddleware> logger)
{
    private readonly string _token = options.Value.BearerToken;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        // Painel administrativo é HTML/JS/CSS estático servido por wwwroot/diagnostics —
        // sem dado nenhum embutido. O token continua exigido em cada chamada que o
        // próprio painel faz aos endpoints /api/diagnostics/* (o usuário digita o token
        // no navegador, guardado só em localStorage).
        if (context.Request.Path.StartsWithSegments("/diagnostics"))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_token))
        {
            logger.LogError("AUTH_CONFIG_ERROR | BearerToken não configurado em appsettings.Production.json");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Server configuration error" });
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        var provided = authHeader["Bearer ".Length..].Trim();
        if (!string.Equals(provided, _token, StringComparison.Ordinal))
        {
            var masked = provided.Length > 6 ? provided[..6] + "***" : "***";
            logger.LogWarning("AUTH_FAILED | token={Masked} path={Path}", masked, context.Request.Path);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await next(context);
    }
}
