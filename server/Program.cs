using System.Security.Claims;
using Fireside;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Threading.RateLimiting;

var initialize = args.Contains("--initialize-database");
var seedDemo = args.Contains("--seed-demo");
var builder = WebApplication.CreateBuilder(args.Where(a => a is not "--initialize-database" and not "--seed-demo").ToArray());
var demo = builder.Configuration.GetValue<bool>("Demo:Enabled");
if (demo && (!builder.Environment.IsDevelopment() ||
    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))))
    throw new InvalidOperationException("Demo authentication is only allowed in local Development.");
if (seedDemo && (!demo || !initialize))
    throw new InvalidOperationException("Sample data requires local demo mode and --initialize-database.");

var sqlite = builder.Configuration["Database:Provider"] == "Sqlite";
if (sqlite && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Production requires shared SQL Server/Azure SQL, not SQLite.");
var connection = builder.Configuration.GetConnectionString("Fireside");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Configure ConnectionStrings:Fireside.");
builder.Services.AddDbContext<FiresideDb>(options =>
{
    if (sqlite) options.UseSqlite(connection);
    else options.UseSqlServer(connection);
});
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (string.IsNullOrWhiteSpace(keyPath))
    throw new InvalidOperationException("Configure a persistent DataProtection:KeyPath.");
Directory.CreateDirectory(keyPath);
builder.Services.AddDataProtection().SetApplicationName("CopilotFiresideChristmas")
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddSingleton<ParticipantIdentity>();
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("participation", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // App Service terminates TLS at a local reverse proxy. Keep the framework's loopback trust boundary.
});
var auth = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
}).AddCookie(options =>
{
    options.Cookie.Name = "__Host-fireside-admin";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(2);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
if (demo)
{
    // The local HTTP demo uses a different cookie, never a production authentication fallback.
    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "fireside-demo-admin";
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}
else if (!builder.Environment.IsEnvironment("Testing"))
{
    var tenant = builder.Configuration["AzureAd:TenantId"];
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var secret = builder.Configuration["AzureAd:ClientSecret"];
    if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(clientId, out _) || string.IsNullOrWhiteSpace(secret))
        throw new InvalidOperationException("Configure AzureAd tenant ID, client ID and client secret for admin sign-in.");
    auth.AddOpenIdConnect(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        options.ClientId = clientId;
        options.ClientSecret = secret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.ValidateIssuer = true;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("EntraAuthentication").LogWarning("Administrator sign-in failed: {FailureType}",
                    context.Failure?.GetType().Name ?? "RemoteFailure");
            context.HandleResponse();
            context.Response.Redirect("/admin?authError=1");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy =>
    policy.RequireAuthenticatedUser().RequireRole("Fireside.Admin")));
var signalr = builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4096);
var signalrConnection = builder.Configuration.GetConnectionString("AzureSignalR");
if (!string.IsNullOrWhiteSpace(signalrConnection))
    signalr.AddAzureSignalR(signalrConnection);
else if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Production requires ConnectionStrings:AzureSignalR.");

var app = builder.Build();
if (initialize)
{
    await DatabaseSetup.Initialize(app.Services, seedDemo);
    return;
}
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self' https://*.service.signalr.net wss://*.service.signalr.net; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if ((context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
        && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            await Results.Problem("Refresh this page and try again.", statusCode: 400).ExecuteAsync(context);
            return;
        }
    }
    await next();
});
app.MapGet("/api/session", (HttpContext context, IAntiforgery csrf, ParticipantIdentity identities) =>
    Results.Ok(new
    {
        participantName = identities.Read(context)?.Name,
        isAdmin = context.User.IsInRole("Fireside.Admin"),
        signedIn = context.User.Identity?.IsAuthenticated == true,
        adminName = context.User.Identity?.Name,
        demo,
        csrfToken = csrf.GetAndStoreTokens(context).RequestToken
    }));
app.MapPut("/api/participant", (NameInput input, HttpContext context, ParticipantIdentity identities) =>
{
    var name = input.Name?.Trim();
    if (!InputRules.Text(name, 2, 60)) return Results.Problem("Use a name between 2 and 60 characters, without control characters.", statusCode: 400);
    var participant = identities.Set(context, name!);
    return Results.Ok(new { participantName = participant.Name });
}).RequireRateLimiting("participation");
app.MapGet("/auth/login", (HttpContext context) => demo
    ? Results.Redirect("/admin")
    : Results.Challenge(new AuthenticationProperties { RedirectUri = "/admin" },
        [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapPost("/auth/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    demo ? [CookieAuthenticationDefaults.AuthenticationScheme] :
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
if (demo)
{
    app.MapPost("/auth/demo-admin", async (HttpContext context) =>
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "local-demo"),
            new Claim(ClaimTypes.Name, "Local demo host"),
            new Claim(ClaimTypes.Role, "Fireside.Admin")
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    });
}
app.MapFiresideApi();
app.MapHub<LiveHub>("/live");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallback(async context =>
{
    if (context.Request.Path != "/" && context.Request.Path != "/admin" && context.Request.Path != "/privacy")
    {
        context.Response.StatusCode = 404;
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(index))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
    }
    else
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync("Frontend not built. Run npm ci && npm run build in client.");
    }
});
await app.RunAsync();

public partial class Program { }
