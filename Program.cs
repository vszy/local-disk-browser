var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Everything requires login except endpoints marked [AllowAnonymous] (the login page).
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.Cookie.Name = "ldb.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // API calls get a 401 instead of an HTML redirect so the page can react.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 401;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Optional: only needed when NOT hosted by IIS/ANCM (which sets the base path itself).
if (app.Configuration["PathBase"] is { Length: > 0 } pathBase) app.UsePathBase(pathBase);

var allowedHosts = app.Configuration.GetSection("Security:AllowedHostNames").Get<string[]>() ?? Array.Empty<string>();

// This app can delete/modify any file the user can, so lock the API down:
// only accept localhost Host headers (DNS rebinding) and require a custom header on
// state-changing calls (a cross-site page cannot send it without a CORS preflight).
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    var local = host == "localhost" || host == "127.0.0.1" || host == "[::1]" || host == "::1";
    if (!local && !allowedHosts.Contains("*") && !allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method)
        && ctx.Request.Headers["X-Requested-With"] != "LocalDiskBrowser")
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    await next();
});

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
