using System.Security.Cryptography;
using System.Text;

namespace StockStudio.Api.Services;

public class SecurityOptions
{
    /// <summary>
    /// When set, every /api request must carry this key in the "X-Api-Key" header
    /// Leave empty for an unauthenticated local run.
    /// Store it in user-secrets / Key Vault, never in appsettings.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Opt-in API key gate. Disabled when no key is configured, so a purely local run stays friction-free;
/// once the app is exposed beyond localhost, setting Security:ApiKey locks down every /api route.
/// The pipeline callback is exempt because it carries its own shared secret.
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _key;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _key = config["Security:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrWhiteSpace(_key) || !ctx.Request.Path.StartsWithSegments("/api"))
        {
            await _next(ctx);
            return;
        }

        // The pipeline callback authenticates with its own secret header.
        if (ctx.Request.Path.StartsWithSegments("/api/pipeline/callback"))
        {
            await _next(ctx);
            return;
        }

        var provided = ctx.Request.Headers["X-Api-Key"].ToString();

        if (!FixedEquals(provided, _key))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "API key mancante o non valida (header X-Api-Key)." });
            return;
        }

        await _next(ctx);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));
}
