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
using Microsoft.IdentityModel.Validators;
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
var adminTenantId = demo ? EntraIdentity.DemoTenant : builder.Configuration["AzureAd:TenantId"];
if (!Guid.TryParse(adminTenantId, out var adminTenant))
    throw new InvalidOperationException("Configure AzureAd:TenantId as the administrative tenant GUID.");
var entra = new EntraIdentity(adminTenant);
builder.Services.AddSingleton(entra);
const string audienceScheme = "EntraAudience";
const string adminScheme = "EntraAdmin";

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
    options.Cookie.Name = "__Host-fireside-auth";
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
        options.Cookie.Name = "fireside-demo-auth";
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}
else if (!builder.Environment.IsEnvironment("Testing"))
{
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var secret = builder.Configuration["AzureAd:ClientSecret"];
    if (!Guid.TryParse(clientId, out _) || string.IsNullOrWhiteSpace(secret))
        throw new InvalidOperationException("Configure AzureAd client ID and client secret for organisational Entra sign-in.");
    void ConfigureEntra(OpenIdConnectOptions options, string authority, string callback, string signoutCallback)
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = secret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.IssuerValidator =
            AadIssuerValidator.GetAadIssuerValidator(options.Authority).Validate;
        options.CallbackPath = callback;
        options.SignedOutCallbackPath = signoutCallback;
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("EntraAuthentication").LogWarning("Entra sign-in failed: {FailureType}",
                    context.Failure?.GetType().Name ?? "RemoteFailure");
            context.HandleResponse();
            context.Response.Redirect(context.Properties?.RedirectUri == "/admin" ? "/admin?authError=1" : "/?authError=1");
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal is null || EntraIdentity.VoterKey(context.Principal) is null)
                context.Fail("An organisational Entra account with valid tenant and object identifiers is required.");
            return Task.CompletedTask;
        };
    }
    auth.AddOpenIdConnect(audienceScheme, options => ConfigureEntra(options,
        "https://login.microsoftonline.com/organizations/v2.0", "/signin-oidc", "/signout-callback-oidc"));
    auth.AddOpenIdConnect(adminScheme, options => ConfigureEntra(options,
        $"https://login.microsoftonline.com/{adminTenant:D}/v2.0", "/signin-oidc-admin", "/signout-callback-oidc-admin"));
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => entra.IsAdmin(context.User)));
    options.AddPolicy("Voter", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => EntraIdentity.VoterKey(context.User) is not null));
});
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
        isAdmin = entra.IsAdmin(context.User),
        canVote = EntraIdentity.VoterKey(context.User) is not null,
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
app.MapGet("/auth/login", (string? destination) => demo
    ? Results.Redirect(destination == "admin" ? "/admin" : "/")
    : Results.Challenge(new AuthenticationProperties { RedirectUri = destination == "admin" ? "/admin" : "/" },
        [destination == "admin" ? adminScheme : audienceScheme]));
app.MapPost("/auth/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    demo ? [CookieAuthenticationDefaults.AuthenticationScheme] :
        [CookieAuthenticationDefaults.AuthenticationScheme, audienceScheme]));
if (demo)
{
    async Task DemoSignIn(HttpContext context, bool admin)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, admin ? "local-demo-host" : "local-demo-voter"),
            new Claim(ClaimTypes.Name, admin ? "Local demo host" : "Local demo voter"),
            new Claim("tid", admin ? EntraIdentity.DemoTenant : EntraIdentity.DemoVoterTenant),
            new Claim("oid", admin ? EntraIdentity.DemoHostObject : EntraIdentity.DemoVoterObject),
            new Claim(ClaimTypes.Role, admin ? "Fireside.Admin" : "Fireside.Attendee")
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    app.MapPost("/auth/demo-admin", (HttpContext context) => DemoSignIn(context, true));
    app.MapPost("/auth/demo-voter", (HttpContext context) => DemoSignIn(context, false));
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
