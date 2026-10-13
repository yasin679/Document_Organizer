using System.Security.Claims;
using DocumentOrganizer.Api;
using DocumentOrganizer.Engine;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

// Command-line helper: dotnet run -- hash-password "S0me-Password!"
if (args.Length >= 2 && args[0] == "hash-password") { Console.WriteLine(Passwords.Hash(args[1])); return; }

var builder = WebApplication.CreateBuilder(args);
var opt = builder.Configuration.GetSection("DocumentOrganizer").Get<OrganizerOptions>() ?? new OrganizerOptions();
var dataPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, opt.DataPath));
Directory.CreateDirectory(dataPath);
opt.DataPath = dataPath;

builder.Services.AddSingleton(opt);
builder.Services.AddSingleton(new SqliteDb(Path.Combine(dataPath, "document-organizer.db")));
builder.Services.AddSingleton<AppDb>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<StorageFactory>();
builder.Services.AddSingleton<Scanner>();
builder.Services.AddSingleton<Processor>();
builder.Services.AddSingleton<Reports>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<DemoData>();

if (opt.BehindReverseProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear(); o.KnownProxies.Clear();
    });

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 300L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 300L * 1024 * 1024);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "docorg.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Max(5, opt.SessionTimeoutMinutes));
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    // Disabled or deleted users lose access on their next request
    o.Events.OnValidatePrincipal = ctx =>
    {
        var name = ctx.Principal?.Identity?.Name;
        var u = name == null ? null : ctx.HttpContext.RequestServices.GetRequiredService<UserStore>().Get(name);
        if (u == null || u.Long("active") == 0 || u.Str("role") != ctx.Principal!.FindFirstValue(ClaimTypes.Role))
        { ctx.RejectPrincipal(); return ctx.HttpContext.SignOutAsync(); }
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.RequireRole("Administrator"));
    o.AddPolicy("Operate", p => p.RequireRole("Administrator", "Operator"));
    o.AddPolicy("View", p => p.RequireRole("Administrator", "Operator", "Viewer"));
});

var app = builder.Build();
if (opt.BehindReverseProxy) app.UseForwardedHeaders();

// Startup tasks
var users = app.Services.GetRequiredService<UserStore>();
var initialPassword = users.EnsureAdmin();
var envPasswordResult = initialPassword == null ? users.ApplyAdminPasswordFromEnvironment() : null;
var jobs = app.Services.GetRequiredService<JobService>();
jobs.RecoverAfterRestart();
jobs.ApplyRetention(dataPath);
if (envPasswordResult == "ok")
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine();
    Console.WriteLine("  Admin password set from DOCORG_ADMIN_PASSWORD. Sign in with user name: admin");
    Console.WriteLine();
    Console.ResetColor();
}
else if (envPasswordResult != null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  " + envPasswordResult);
    Console.ResetColor();
}
if (initialPassword != null)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine();
    Console.WriteLine("  ================================================================");
    Console.WriteLine("   Document Organizer - first run");
    Console.WriteLine("   Sign in with user name: admin");
    Console.WriteLine($"   Temporary password:     {initialPassword}");
    Console.WriteLine("   Change it under Settings > My password after signing in.");
    Console.WriteLine("  ================================================================");
    Console.WriteLine();
    Console.ResetColor();
}

// Security headers
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});

// CSRF protection for the JSON API: state-changing calls must carry a custom header (cannot be sent cross-site without CORS).
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Headers["X-DocOrg"] != "1")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-DocOrg request header." });
        return;
    }
    await next();
});

// Liveness probe for containers and load balancers (no authentication, no data).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.UseApiErrors();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers["Cache-Control"] = "no-cache"
});
app.UseAuthentication();
app.UseAuthorization();

app.MapOrganizerApi();

app.Run();
