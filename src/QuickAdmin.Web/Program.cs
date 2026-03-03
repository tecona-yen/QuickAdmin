using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using QuickAdmin.Shared;

var basePath = AppContext.BaseDirectory;
var configPath = Path.Combine(basePath, "config", "quickadmin.json");
var logPath = Path.Combine(basePath, "logs", "audit.log");
var configStore = new ConfigStore(configPath);
var config = configStore.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(new AuditLog(logPath));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BruteForceStore>();
builder.Services.AddHttpClient("agent", c => c.BaseAddress = new Uri("http://127.0.0.1:8610"));
builder.Services.Configure<FormOptions>(o => o.ValueLengthLimit = 1024 * 1024);

builder.WebHost.ConfigureKestrel(o =>
{
    var ip = config.Server.AllowLanAccess ? IPAddress.Any : IPAddress.Loopback;
    o.Listen(ip, 8600);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

bool IsAuthed(HttpContext ctx, SessionStore sessions, out SessionData? session)
{
    session = null;
    if (!ctx.Request.Cookies.TryGetValue("qa_session", out var token)) return false;
    if (!sessions.TryGet(token, out session)) return false;
    if (session.ExpiresAt < DateTimeOffset.UtcNow) { sessions.Remove(token); return false; }
    session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.SessionTimeoutMinutes);
    return true;
}

bool CheckCsrf(HttpContext ctx, SessionData s) =>
    ctx.Request.Headers.TryGetValue("X-CSRF-Token", out var v) && v == s.CsrfToken;

app.MapGet("/health", async (IHttpClientFactory hf) =>
{
    var client = hf.CreateClient("agent");
    var rsp = await client.GetAsync("/health");
    return Results.Ok(new { web = "ok", agent = rsp.IsSuccessStatusCode ? "ok" : "down" });
});

app.MapGet("/", (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out _)) return Results.Redirect("/login.html");
    return Results.Redirect("/index.html");
});

app.MapPost("/api/login", async (HttpContext ctx, SessionStore sessions, BruteForceStore brute) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (brute.IsLocked(ip, out var wait)) return Results.Json(new { error = $"Locked. retry in {wait}s" }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
    if (req is null || string.IsNullOrWhiteSpace(req.Password)) return Results.BadRequest();

    var current = configStore.Load();
    if (!PasswordUtil.Verify(req.Password, current.Auth))
    {
        brute.Fail(ip);
        return Results.Json(new { error = "Invalid password" }, statusCode: 401);
    }

    brute.Success(ip);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    sessions.Set(token, new SessionData { CsrfToken = csrf, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(current.SessionTimeoutMinutes) });
    ctx.Response.Cookies.Append("qa_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromMinutes(current.SessionTimeoutMinutes) });
    return Results.Ok(new { csrf, timeoutMinutes = current.SessionTimeoutMinutes });
});

app.MapPost("/api/logout", (HttpContext ctx, SessionStore sessions) =>
{
    if (ctx.Request.Cookies.TryGetValue("qa_session", out var token)) sessions.Remove(token);
    ctx.Response.Cookies.Delete("qa_session");
    return Results.Ok();
});

app.MapGet("/api/bootstrap", (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out var s)) return Results.Unauthorized();
    var c = configStore.Load();
    return Results.Ok(new { csrf = s!.CsrfToken, sessionTimeoutMinutes = c.SessionTimeoutMinutes, wolTargets = c.WolTargets, customCommands = c.CustomCommands });
});

app.MapPost("/api/settings/password", async (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out var s)) return Results.Unauthorized();
    if (!CheckCsrf(ctx, s!)) return Results.StatusCode(403);
    var req = await ctx.Request.ReadFromJsonAsync<SetPasswordRequest>();
    if (req is null || req.Password.Length < 4) return Results.BadRequest();
    var c = configStore.Load();
    var (hash, salt) = PasswordUtil.HashPassword(req.Password, c.Auth.Iterations);
    c.Auth.PasswordHash = hash;
    c.Auth.PasswordSalt = salt;
    configStore.Save(c);
    return Results.Ok();
});

app.MapPost("/api/settings/network", async (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out var s)) return Results.Unauthorized();
    if (!CheckCsrf(ctx, s!)) return Results.StatusCode(403);
    var req = await ctx.Request.ReadFromJsonAsync<NetworkSettingRequest>();
    if (req is null) return Results.BadRequest();
    var c = configStore.Load();
    c.Server.AllowLanAccess = req.AllowLan;
    c.Server.BindAddress = req.AllowLan ? "0.0.0.0" : "127.0.0.1";
    configStore.Save(c);
    return Results.Ok(new { note = "Restart web service to apply bind address change." });
});

MapAgentProxy("/api/performance", "/performance", "GET");
MapAgentProxy("/api/processes", "/processes", "GET");
MapAgentProxy("/api/network/adapters", "/network/adapters", "GET");
MapAgentProxy("/api/iis/status", "/iis/status", "GET");
MapAgentProxy("/api/system/shutdown", "/system/shutdown", "POST", audit: "shutdown");
MapAgentProxy("/api/system/restart", "/system/restart", "POST", audit: "restart");
MapAgentProxy("/api/iis/action/{verb}", "/iis/action/{verb}", "POST", audit: "iis_action");
MapAgentProxy("/api/processes/{pid}/kill", "/processes/{pid}/kill", "POST", audit: "kill_process");
MapAgentProxy("/api/network/adapters/{id}/toggle", "/network/adapters/{id}/toggle", "POST", audit: "toggle_adapter");
MapAgentProxy("/api/wol/send", "/wol/send", "POST", audit: "wol_send");
MapAgentProxy("/api/terminal/start", "/terminal/start", "POST");
MapAgentProxy("/api/terminal/{id}/input", "/terminal/{id}/input", "POST");
MapAgentProxy("/api/terminal/{id}/output", "/terminal/{id}/output", "GET");
MapAgentProxy("/api/terminal/{id}/stop", "/terminal/{id}/stop", "POST");

app.MapGet("/api/config", (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out _)) return Results.Unauthorized();
    return Results.Ok(configStore.Load());
});
app.MapPost("/api/config", async (HttpContext ctx, SessionStore sessions) =>
{
    if (!IsAuthed(ctx, sessions, out var s)) return Results.Unauthorized();
    if (!CheckCsrf(ctx, s!)) return Results.StatusCode(403);
    var req = await ctx.Request.ReadFromJsonAsync<QuickAdminConfig>();
    if (req is null) return Results.BadRequest();
    req.Server.Port = 8600;
    configStore.Save(req);
    return Results.Ok();
});

app.Run();

void MapAgentProxy(string route, string target, string method, string? audit = null)
{
    if (method == "GET")
    {
        app.MapGet(route, async (HttpContext ctx, IHttpClientFactory hf, SessionStore sessions) => await Proxy(ctx, hf, sessions, target, HttpMethod.Get, audit));
    }
    else
    {
        app.MapPost(route, async (HttpContext ctx, IHttpClientFactory hf, SessionStore sessions) => await Proxy(ctx, hf, sessions, target, HttpMethod.Post, audit));
    }
}

async Task<IResult> Proxy(HttpContext ctx, IHttpClientFactory hf, SessionStore sessions, string target, HttpMethod method, string? audit)
{
    if (!IsAuthed(ctx, sessions, out var s)) return Results.Unauthorized();
    if (method != HttpMethod.Get && !CheckCsrf(ctx, s!)) return Results.StatusCode(403);
    var client = hf.CreateClient("agent");
    var finalTarget = target;
    foreach (var kv in ctx.Request.RouteValues) finalTarget = finalTarget.Replace("{" + kv.Key + "}", kv.Value?.ToString());
    HttpResponseMessage rsp;
    if (method == HttpMethod.Get)
    {
        rsp = await client.GetAsync(finalTarget + ctx.Request.QueryString);
    }
    else
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        rsp = await client.PostAsync(finalTarget, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
    }

    var payload = await rsp.Content.ReadAsStringAsync();
    if (audit is not null)
    {
        var lg = ctx.RequestServices.GetRequiredService<AuditLog>();
        lg.Write($"{audit} by session {ctx.Request.Cookies["qa_session"]}");
    }
    return Results.Content(payload, rsp.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)rsp.StatusCode);
}

record LoginRequest(string Password);
record SetPasswordRequest(string Password);
record NetworkSettingRequest(bool AllowLan);

class SessionStore
{
    private readonly ConcurrentDictionary<string, SessionData> _store = new();
    public void Set(string token, SessionData data) => _store[token] = data;
    public bool TryGet(string token, out SessionData? data) => _store.TryGetValue(token, out data);
    public void Remove(string token) => _store.TryRemove(token, out _);
}

class SessionData
{
    public string CsrfToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

class BruteForceStore
{
    private readonly ConcurrentDictionary<string, (int count, DateTimeOffset until)> _store = new();
    public void Fail(string ip)
    {
        var cur = _store.GetOrAdd(ip, (0, DateTimeOffset.MinValue));
        var count = cur.count + 1;
        var until = count >= 5 ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MinValue;
        _store[ip] = (count, until);
    }
    public void Success(string ip) => _store.TryRemove(ip, out _);
    public bool IsLocked(string ip, out int seconds)
    {
        seconds = 0;
        if (!_store.TryGetValue(ip, out var v)) return false;
        if (v.until <= DateTimeOffset.UtcNow) return false;
        seconds = (int)(v.until - DateTimeOffset.UtcNow).TotalSeconds;
        return true;
    }
}

class AuditLog
{
    private readonly string _path;
    private readonly object _gate = new();
    public AuditLog(string path) => _path = path;
    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}");
        }
    }
}
