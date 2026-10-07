using Jaq;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var remoteHosting = builder.Configuration.GetValue<bool>("JAQ_REMOTE_HOSTING");
if (remoteHosting)
{
    var host = builder.Configuration["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(host) || host.Contains('*') || host.Contains(';') || Uri.CheckHostName(host) != UriHostNameType.Dns)
        throw new InvalidOperationException("Defina AllowedHosts com o domínio do salão para hospedagem remota.");
    if (!IPAddress.TryParse(builder.Configuration["JAQ_TRUSTED_PROXY"], out var proxy))
        throw new InvalidOperationException("Defina JAQ_TRUSTED_PROXY com o IP do proxy HTTPS.");
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); o.KnownProxies.Add(proxy);
    });
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var dataDir = Path.GetFullPath(builder.Configuration["JAQ_DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
if (remoteHosting && !File.Exists(Path.Combine(dataDir, "jaq.db")))
    throw new InvalidOperationException("Transfira o banco já configurado do salão antes de iniciar a hospedagem remota.");
Directory.CreateDirectory(dataDir);
// Prevent two application processes from checking balances against the same database independently.
using var instanceLock = new FileStream(Path.Combine(dataDir, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "jaq.db") }.ToString();
builder.Services.AddDbContext<SalonDb>(o => o.UseSqlite(connectionString));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddSingleton<SemaphoreSlim>(new SemaphoreSlim(1, 1));
builder.Services.AddScoped<IPasswordHasher<Staff>, PasswordHasher<Staff>>();
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = remoteHosting ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "Jaq.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = remoteHosting ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c =>
    {
        var db = c.HttpContext.RequestServices.GetRequiredService<SalonDb>();
        if (!int.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var personId))
        { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); return; }
        var person = await db.Staff.FindAsync(personId);
        if (person is null || !person.Active || !KnownRole(person.Role) || person.Role != c.Principal!.FindFirstValue(ClaimTypes.Role) || person.SecurityVersion.ToString() != c.Principal.FindFirstValue("version"))
        { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (remoteHosting)
{
    app.UseForwardedHeaders();
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Acesse pelo endereço HTTPS do salão." }); return; }
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
        await next();
    });
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SalonDb>();
    if (remoteHosting && !db.Staff.Any(x => x.Role == "owner" && x.Active && x.Username != null && x.PasswordHash != null))
        throw new InvalidOperationException("O banco remoto precisa conter a conta ativa da dona já configurada.");
    db.Database.EnsureCreated();
    if (!db.Staff.Any())
    {
        db.Staff.AddRange(Enumerable.Range(1, 10).Select(i => new Staff { Name = $"Atendente {i:00}" }));
        db.Staff.AddRange(new Staff { Name = "Funcionária 01", Role = "receptionist", CanProvide = false }, new Staff { Name = "Funcionária 02", Role = "receptionist", CanProvide = false }, new Staff { Name = "Gerente", Role = "manager", CanProvide = false }, new Staff { Name = "Dona", Role = "owner" });
    }
    if (!db.Services.Any())
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(app.Environment.ContentRootPath, "Seed", "services.json")));
        foreach (var row in source.RootElement.EnumerateArray())
            db.Services.Add(new Service { Name = row.GetProperty("servico").GetString()!, Price = (long)(decimal.Parse(row.GetProperty("valor_cobrado_cliente_brl").GetString()!, CultureInfo.InvariantCulture) * 100), Payout = (long)(decimal.Parse(row.GetProperty("valor_recebido_atendente_brl").GetString()!, CultureInfo.InvariantCulture) * 100), Notes = row.GetProperty("observacoes").GetString()! });
    }
    db.SaveChanges();
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers.CacheControl = "no-cache, no-store";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.Pragma = "no-cache";
    try { await next(); }
    catch (RuleException ex) { ctx.Response.StatusCode = ex.Code; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Sessão expirada. Atualize a página." }); }
    catch (DbUpdateConcurrencyException) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Este registro foi alterado. Atualize a página e tente novamente." }); }
    catch (DbUpdateException ex) { app.Logger.LogError(ex, "Database operation failed"); ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } ? "Este usuário já existe. Escolha outro nome de acesso." : "Não foi possível salvar este registro. Atualize a página e tente novamente." }); }
    catch (BadHttpRequestException ex) { ctx.Response.StatusCode = ex.StatusCode; await ctx.Response.WriteAsJsonAsync(new { error = "Dados inválidos. Confira os campos enviados." }); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!ctx.Response.HasStarted) { app.Logger.LogError(ex, "Request failed: {Path}", ctx.Request.Path); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "Ocorreu um erro interno. Tente novamente; se persistir, consulte o registro do servidor." }); }
});
app.UseStaticFiles();
app.UseRateLimiter();
// Include authentication in the serialized section so queued requests cannot use revoked identities.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var gate = ctx.RequestServices.GetRequiredService<SemaphoreSlim>();
        await gate.WaitAsync(ctx.RequestAborted);
        try { await next(); } finally { gate.Release(); }
    }
    else await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method is not ("GET" or "HEAD"))
        await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
    await next();
});
app.MapGet("/api/csrf", (HttpContext ctx, IAntiforgery anti) => Results.Ok(new { token = anti.GetAndStoreTokens(ctx).RequestToken }));
app.MapGet("/api/session", async (HttpContext ctx, SalonDb db) => new { application = "JaqAlongamentos", version = typeof(Staff).Assembly.GetName().Version?.ToString(), setupNeeded = !await db.Staff.AnyAsync(x => x.PasswordHash != null), user = ctx.User.Identity?.IsAuthenticated == true ? new { id = Actor(ctx), name = ctx.User.Identity.Name, role = ctx.User.FindFirstValue(ClaimTypes.Role) } : null });
app.MapPost("/api/setup", async (SetupInput input, HttpContext ctx, SalonDb db, IPasswordHasher<Staff> hash) =>
{
    Must(!await db.Staff.AnyAsync(x => x.PasswordHash != null), "A configuração inicial já foi feita.", 409);
    var owner = await db.Staff.SingleAsync(x => x.Role == "owner");
    owner.Name = Text(input.Name, "Nome", 100); owner.Username = Username(input.Username); Password(input.Password); owner.PasswordHash = hash.HashPassword(owner, input.Password);
    Log(db, owner.Id, "setup", "Conta da dona configurada"); await db.SaveChangesAsync(); await SignIn(ctx, owner); return Results.Ok();
}).RequireRateLimiting("login");
app.MapPost("/api/login", async (LoginInput input, HttpContext ctx, SalonDb db, IPasswordHasher<Staff> hash) =>
{
    var username = Optional(input.Username, 60).ToLowerInvariant();
    var person = await db.Staff.SingleOrDefaultAsync(x => x.Username == username && x.Active);
    Must(person?.PasswordHash != null && KnownRole(person.Role) && input.Password is { Length: >= 1 and <= 256 } && hash.VerifyHashedPassword(person!, person!.PasswordHash!, input.Password) != PasswordVerificationResult.Failed, "Usuário ou senha incorretos.", 401);
    await SignIn(ctx, person!); return Results.Ok();
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/data", async (HttpContext ctx, SalonDb db) =>
{
    var attendant = ctx.User.IsInRole("attendant"); var id = Actor(ctx);
    var visitQuery = db.Visits.AsNoTracking().AsSplitQuery().Include(x => x.Items).Include(x => x.Payments).Include(x => x.Client).AsQueryable();
    if (attendant) visitQuery = visitQuery.Where(x => x.Items.Any(i => !i.Removed && i.StaffId == id));
    var visits = await visitQuery.OrderByDescending(x => x.Id).ToListAsync();
    var clientsQuery = db.Clients.AsNoTracking().AsQueryable();
    if (attendant) clientsQuery = clientsQuery.Where(c => db.Visits.Any(v => v.ClientId == c.Id && v.Items.Any(i => !i.Removed && i.StaffId == id)));
    var clients = await clientsQuery.OrderBy(x => x.Name).ToListAsync();
    var services = await db.Services.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    return Results.Ok(new
    {
        staff = await db.Staff.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Role, x.Active, x.CanProvide, hasAccess = x.Username != null && x.PasswordHash != null, revision = x.SecurityVersion.ToString(), username = attendant ? null : x.Username }).ToListAsync(),
        services = services.Select(RevisionRow), clients = clients.Select(RevisionRow),
        visits = visits.Select(x => new { x.Id, x.ClientId, clientName = x.Client.Name, x.Status, x.Notes, x.CreatedAt, x.Version, items = x.Items.Where(i => !i.Removed && (!attendant || i.StaffId == id)), payments = attendant ? [] : x.Payments }),
        audits = ctx.User.IsInRole("owner") || ctx.User.IsInRole("manager") ? await db.Audits.AsNoTracking().Where(x => !x.Action.StartsWith("request:")).OrderByDescending(x => x.Id).Take(100).ToListAsync() : []
    });
});
api.MapPost("/staff", async (StaffInput input, HttpContext ctx, SalonDb db, IPasswordHasher<Staff> hash) =>
{
    Manage(ctx); var replay = await TryReplay(ctx, db, input); if (replay.HasValue) return Results.Ok(new { id = replay.Value });
    var person = new Staff(); ApplyStaff(person, input, ctx, hash); await UniqueUsername(db, person);
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Staff.Add(person); Log(db, Actor(ctx), "staff.add", person.Name); await db.SaveChangesAsync(); RecordReceipt(ctx, db, input, person.Id); await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(new { person.Id });
});
api.MapPut("/staff/{id:int}", async (int id, StaffInput input, HttpContext ctx, SalonDb db, IPasswordHasher<Staff> hash) =>
{
    Manage(ctx); var person = await db.Staff.FindAsync(id) ?? throw new RuleException("Pessoa não encontrada.", 404);
    Must(ctx.User.IsInRole("owner") || person.Role == "attendant", "A gerente pode administrar atendentes, mas não outros perfis.", 403);
    Must(id != Actor(ctx) || (input.Active && input.Role == person.Role), "Você não pode desativar sua conta ou mudar seu próprio perfil.");
    Must(person.Role != "owner" || (input.Role == "owner" && input.Active), "A conta da dona deve permanecer ativa.");
    CheckRevision(ctx, person.SecurityVersion.ToString(CultureInfo.InvariantCulture));
    var original = Fingerprint(person);
    var before = new { person.Name, person.Role, person.Active, person.CanProvide, person.Username, person.PasswordHash };
    ApplyStaff(person, input, ctx, hash); await UniqueUsername(db, person);
    if (original == Fingerprint(person)) return Results.Ok();
    var changes = new List<string>();
    if (before.Name != person.Name) changes.Add($"nome: {before.Name} → {person.Name}");
    if (before.Role != person.Role) changes.Add($"perfil: {RoleLabel(before.Role)} → {RoleLabel(person.Role)}");
    if (before.Active != person.Active) changes.Add(person.Active ? "reativada" : "desativada");
    if (before.CanProvide != person.CanProvide) changes.Add(person.CanProvide ? "habilitada para realizar serviços" : "atendimento de serviços desabilitado");
    if (before.Username != person.Username) changes.Add(person.Username is null ? "acesso ao sistema removido" : "usuário de acesso atualizado");
    if (before.PasswordHash != person.PasswordHash && person.PasswordHash is not null) changes.Add(before.PasswordHash is null ? "senha de acesso definida" : "senha de acesso redefinida");
    person.SecurityVersion++; Log(db, Actor(ctx), "staff.update", $"Cadastro de {person.Name} atualizado: {string.Join("; ", changes)}."); await db.SaveChangesAsync(); if (id == Actor(ctx)) await SignIn(ctx, person); return Results.Ok();
});
api.MapPut("/services/{id:int}", async (int id, ServiceInput input, HttpContext ctx, SalonDb db) =>
{
    Manage(ctx); var service = await db.Services.FindAsync(id) ?? throw new RuleException("Serviço não encontrado.", 404);
    CheckRevision(ctx, Fingerprint(service));
    Money(input.Price); Money(input.Payout); Must(input.Payout <= input.Price, "O valor da atendente não pode superar o valor cobrado.");
    Log(db, Actor(ctx), "service.update", $"{service.Name}: {Reais(service.Price)}/{Reais(service.Payout)} → {Reais(input.Price)}/{Reais(input.Payout)}");
    service.Name = Text(input.Name, "Serviço", 150); service.Price = input.Price; service.Payout = input.Payout; service.Reviewed = input.Reviewed; service.Notes = Optional(input.Notes, 2000); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/clients", async (Client input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var replay = await TryReplay(ctx, db, input); if (replay.HasValue) return Results.Ok(new { id = replay.Value });
    var originalFingerprint = Fingerprint(input); input.Id = 0; ValidateClient(input);
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Clients.Add(input); Log(db, Actor(ctx), "client.add", input.Name); await db.SaveChangesAsync(); RecordReceipt(ctx, db, input, input.Id, originalFingerprint); await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(new { input.Id });
});
api.MapPut("/clients/{id:int}", async (int id, Client input, HttpContext ctx, SalonDb db) =>
{
    var client = await db.Clients.FindAsync(id) ?? throw new RuleException("Cliente não encontrada.", 404);
    var actorId = Actor(ctx);
    if (ctx.User.IsInRole("attendant")) Must(await db.Visits.AnyAsync(v => v.ClientId == id && v.Items.Any(i => i.StaffId == actorId && !i.Removed)), "Você não atende esta cliente.", 403);
    CheckRevision(ctx, Fingerprint(client));
    ValidateClient(input); input.Id = id; db.Entry(client).CurrentValues.SetValues(input); Log(db, Actor(ctx), "client.update", input.Name); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/visits", async (VisitInput input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); Must(await db.Clients.AnyAsync(x => x.Id == input.ClientId), "Escolha uma cliente cadastrada.");
    var replay = await TryReplay(ctx, db, input); if (replay.HasValue) return Results.Ok(new { id = replay.Value });
    var visit = new Visit { ClientId = input.ClientId, Notes = Optional(input.Notes, 2000) };
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Visits.Add(visit); await db.SaveChangesAsync(); Log(db, Actor(ctx), "visit.add", $"Comanda {visit.Id}"); RecordReceipt(ctx, db, input, visit.Id); await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(new { visit.Id });
});
api.MapPost("/visits/{id:int}/items", async (int id, ItemInput input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var visit = await GetVisit(db, id); Editable(visit); CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture)); var service = await db.Services.FindAsync(input.ServiceId) ?? throw new RuleException("Serviço não encontrado.");
    var person = await Provider(db, input.StaffId); ValidateSchedule(input.StartsAt, input.Minutes); await NoConflict(db, input.StaffId, input.StartsAt, input.Minutes);
    var payout = Payout(person, input.Payout, service.Payout, service.Price, ctx);
    visit.Items.Add(new VisitItem { ServiceId = service.Id, ServiceName = service.Name, StaffId = person.Id, StaffName = person.Name, Price = service.Price, Payout = payout, StartsAt = DateTime.SpecifyKind(input.StartsAt, DateTimeKind.Unspecified), Minutes = input.Minutes }); visit.Version++;
    Log(db, Actor(ctx), "item.add", $"Comanda {id}: {service.Name}; {person.Name}; cobrado {Reais(service.Price)}; comissão {Reais(payout)}"); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPut("/visits/{id:int}/items/{itemId:int}", async (int id, int itemId, AssignmentInput input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var visit = await GetVisit(db, id); Editable(visit); CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture)); var item = visit.Items.SingleOrDefault(x => x.Id == itemId && !x.Removed) ?? throw new RuleException("Item não encontrado.", 404);
    var person = await Provider(db, input.StaffId); ValidateSchedule(input.StartsAt, input.Minutes); await NoConflict(db, person.Id, input.StartsAt, input.Minutes, itemId);
    if (item.StaffId != person.Id && (await db.Staff.FindAsync(item.StaffId))?.Role == "owner")
    { Manage(ctx); Must(input.Payout.HasValue, "Ao transferir um serviço da dona, defina explicitamente a comissão da nova responsável."); }
    var payout = Payout(person, input.Payout, item.Payout, item.Price, ctx);
    Log(db, Actor(ctx), "item.update", $"Comanda {id}: {item.ServiceName}; {item.StaffName}/{Reais(item.Payout)} → {person.Name}/{Reais(payout)}"); item.StaffId = person.Id; item.StaffName = person.Name; item.Payout = payout; item.StartsAt = DateTime.SpecifyKind(input.StartsAt, DateTimeKind.Unspecified); item.Minutes = input.Minutes; visit.Version++; await db.SaveChangesAsync(); return Results.Ok();
});
api.MapDelete("/visits/{id:int}/items/{itemId:int}", async (int id, int itemId, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var visit = await GetVisit(db, id); Editable(visit); CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture)); var item = visit.Items.SingleOrDefault(x => x.Id == itemId && !x.Removed) ?? throw new RuleException("Item não encontrado.", 404);
    Must(visit.Items.Where(x => !x.Removed && x.Id != itemId).Sum(x => x.Price) >= visit.Payments.Sum(x => x.Amount), "Este item já tem recebimento vinculado. O saldo não pode ficar negativo.");
    item.Removed = true; visit.Version++; Log(db, Actor(ctx), "item.remove", $"Comanda {id}: {item.ServiceName}"); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/visits/{id:int}/status", async (int id, StatusInput input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var visit = await GetVisit(db, id); Editable(visit);
    CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture));
    Must(new[] { "scheduled", "confirmed", "inProgress", "completed", "cancelled", "noShow" }.Contains(input.Status), "Situação inválida.");
    if (input.Status is "cancelled" or "noShow") Must(visit.Payments.Count == 0, "Há valores recebidos. Resolva o estorno antes de cancelar; estornos serão tratados em uma próxima versão.");
    if (input.Status == "completed") { Must(visit.Items.Any(x => !x.Removed), "Adicione pelo menos um serviço."); foreach (var item in visit.Items.Where(x => !x.Removed)) await Provider(db, item.StaffId); }
    Log(db, Actor(ctx), "visit.status", $"Comanda {id}: {visit.Status} → {input.Status}"); visit.Status = input.Status; visit.Version++; await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/visits/{id:int}/payments", async (int id, PaymentInput input, HttpContext ctx, SalonDb db) =>
{
    Operational(ctx); var visit = await GetVisit(db, id); Must(visit.Status is not ("cancelled" or "noShow"), "Comanda cancelada.");
    CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture));
    Money(input.Amount); Must(input.Amount > 0, "Informe um valor maior que zero."); Must(new[] { "Pix", "Dinheiro", "Cartão de crédito", "Cartão de débito" }.Contains(input.Method), "Forma de pagamento inválida."); Must(input.Kind is "deposit" or "payment", "Tipo de recebimento inválido.");
    Must(input.Amount <= visit.Items.Where(x => !x.Removed).Sum(x => x.Price) - visit.Payments.Sum(x => x.Amount), "O recebimento supera o saldo da comanda.");
    visit.Payments.Add(new Payment { Amount = input.Amount, Method = input.Method, Kind = input.Kind }); visit.Version++; Log(db, Actor(ctx), "payment.add", $"Comanda {id}: {Reais(input.Amount)}; {input.Method}; {(input.Kind == "deposit" ? "sinal" : "pagamento")}"); await db.SaveChangesAsync(); return Results.Ok();
});
api.MapPost("/commissions/{itemId:int}/pay", async (int itemId, HttpContext ctx, SalonDb db) =>
{
    Manage(ctx); var item = await db.Items.FindAsync(itemId) ?? throw new RuleException("Item não encontrado.", 404); var visit = await GetVisit(db, item.VisitId);
    CheckRevision(ctx, visit.Version.ToString(CultureInfo.InvariantCulture));
    Must(visit.Status == "completed" && !item.Removed, "A comissão deve pertencer a um atendimento concluído."); Must(item.PayoutPaidAt is null, "Comissão já registrada como paga."); item.PayoutPaidAt = DateTime.UtcNow; visit.Version++; Log(db, Actor(ctx), "commission.pay", $"Item {itemId}: {item.StaffName}; {Reais(item.Payout)}"); await db.SaveChangesAsync(); return Results.Ok();
});
api.Map("/{**path}", () => Results.NotFound(new { error = "Recurso não encontrado." }));
app.MapFallbackToFile("index.html");
app.Run();

static int Actor(HttpContext c) => int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
static bool KnownRole(string? role) => role is "owner" or "manager" or "receptionist" or "attendant";
static string RoleLabel(string role) => role switch { "owner" => "Dona", "manager" => "Gerente", "receptionist" => "Funcionária / recepção", "attendant" => "Atendente", _ => "Perfil desconhecido" };
static void Must(bool value, string message, int code = 400) { if (!value) throw new RuleException(message, code); }
static void Manage(HttpContext c) => Must(c.User.IsInRole("owner") || c.User.IsInRole("manager"), "Acesso restrito à gerente e à dona.", 403);
static void Operational(HttpContext c) => Must(c.User.IsInRole("owner") || c.User.IsInRole("manager") || c.User.IsInRole("receptionist"), "Seu perfil permite consultar seus atendimentos e editar suas fichas técnicas.", 403);
static string Text(string? value, string label, int max) { var s = value?.Trim() ?? ""; Must(s.Length > 0 && s.Length <= max, $"{label}: informe de 1 a {max} caracteres."); return s; }
static string Optional(string? s, int max) { s ??= ""; Must(s.Length <= max, $"Texto deve ter até {max} caracteres."); return s.Trim(); }
static string Username(string? s) { s = Text(s, "Usuário", 60).ToLowerInvariant(); Must(s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@'), "Use letras sem acento, números, ponto, hífen ou @ no usuário."); return s; }
static void Password(string? s) => Must(s is { Length: >= 10 and <= 256 }, "A senha deve ter entre 10 e 256 caracteres.");
static async Task UniqueUsername(SalonDb db, Staff person)
{
    if (person.Username is not null) Must(!await db.Staff.AnyAsync(s => s.Username == person.Username && s.Id != person.Id), "Este usuário já existe. Escolha outro nome de acesso.", 409);
}
static string Reais(long value) => (value / 100m).ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
static void Money(long value) => Must(value is >= 0 and <= 100_000_000, "Valor inválido. Use até R$ 1.000.000,00.");
static void Log(SalonDb db, int actor, string action, string detail) => db.Audits.Add(new Audit { ActorId = actor, Action = action, Detail = detail });
static async Task SignIn(HttpContext c, Staff p) => await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()), new Claim(ClaimTypes.Name, p.Name), new Claim(ClaimTypes.Role, p.Role), new Claim("version", p.SecurityVersion.ToString()) }, CookieAuthenticationDefaults.AuthenticationScheme)));
static void ApplyStaff(Staff p, StaffInput input, HttpContext c, IPasswordHasher<Staff> hash)
{
    Must(KnownRole(input.Role), "Perfil inválido.");
    Must(c.User.IsInRole("owner") || input.Role == "attendant", "A gerente só pode cadastrar ou alterar atendentes.", 403);
    Must(p.Role == "owner" || input.Role != "owner", "Existe uma conta de dona. Não é possível criar outra por esta tela.");
    p.Name = Text(input.Name, "Nome", 100); p.Role = input.Role; p.Active = input.Active; p.CanProvide = input.CanProvide;
    p.Username = string.IsNullOrWhiteSpace(input.Username) ? null : Username(input.Username);
    if (!string.IsNullOrEmpty(input.Password)) { Password(input.Password); Must(p.Username != null, "Informe o usuário para definir uma senha."); p.PasswordHash = hash.HashPassword(p, input.Password); }
    if (p.Username is null) p.PasswordHash = null;
    Must(p.Role != "owner" || (p.Username != null && p.PasswordHash != null), "O acesso da dona não pode ser removido. Mantenha o usuário e a senha.");
    Must(p.Username is null || p.PasswordHash != null, "Defina uma senha para habilitar este usuário.");
}
static void ValidateClient(Client c)
{
    c.Name = Text(c.Name, "Nome da cliente", 100); c.Phone = Optional(c.Phone, 30); c.Preferences = Optional(c.Preferences, 2000); c.ChemicalHistory = Optional(c.ChemicalHistory, 4000); c.BlondGoal = Optional(c.BlondGoal, 2000); c.Formula = Optional(c.Formula, 4000); c.StrandTest = Optional(c.StrandTest, 2000); c.MegaTechnique = Optional(c.MegaTechnique, 500); c.HairOrigin = Optional(c.HairOrigin, 500); c.HairColor = Optional(c.HairColor, 500); c.HairLength = Optional(c.HairLength, 100); c.HairQuantity = Optional(c.HairQuantity, 100); Must(new[] { "Pendente", "Contatada", "Reagendada", "Sem resposta" }.Contains(c.ReturnContact), "Situação de retorno inválida.");
    Must(c.InstallationDate is null || c.InstallationDate.Value.Year is >= 1900 and <= 2100, "Data de instalação fora do intervalo permitido.");
    Must(c.NextReturn is null || c.NextReturn.Value.Year is >= 1900 and <= 2100, "Data de retorno fora do intervalo permitido.");
}
static async Task<Visit> GetVisit(SalonDb db, int id) => await db.Visits.AsSplitQuery().Include(x => x.Items).Include(x => x.Payments).SingleOrDefaultAsync(x => x.Id == id) ?? throw new RuleException("Comanda não encontrada.", 404);
static void Editable(Visit v) => Must(v.Status is not ("completed" or "cancelled" or "noShow"), "Esta comanda está encerrada e seu histórico foi preservado.");
static async Task<Staff> Provider(SalonDb db, int id) { var p = await db.Staff.FindAsync(id); Must(p is { Active: true, CanProvide: true }, "Escolha uma profissional ativa e habilitada para atender."); return p!; }
static long Payout(Staff p, long? amount, long fallback, long price, HttpContext ctx)
{
    if (p.Role == "owner") Must(amount.HasValue, "Informe explicitamente o valor da dona para este serviço; pode ser zero.");
    var value = amount ?? fallback; Money(value); Must(value <= price, "Comissão não pode superar o valor cobrado.");
    if (value != fallback) Manage(ctx); return value;
}
static void ValidateSchedule(DateTime start, int minutes) { Must(start.Year is >= 2020 and <= 2100 && start.Kind == DateTimeKind.Unspecified && start.Ticks % TimeSpan.TicksPerMinute == 0, "Informe um horário local de Brasília, sem fuso ou segundos."); Must(minutes is >= 5 and <= 720, "A duração deve ser de 5 a 720 minutos."); }
static async Task NoConflict(SalonDb db, int staffId, DateTime start, int minutes, int excludeId = 0)
{
    var end = start.AddMinutes(minutes);
    var items = await db.Items.AsNoTracking().Where(i => i.StaffId == staffId && i.Id != excludeId && !i.Removed && db.Visits.Any(v => v.Id == i.VisitId && v.Status != "cancelled" && v.Status != "noShow") && i.StartsAt < end && i.StartsAt > start.AddHours(-12)).ToListAsync();
    Must(!items.Any(i => i.StartsAt.AddMinutes(i.Minutes) > start), "Esta profissional já tem um serviço nesse horário.", 409);
}
static string Fingerprint(object row)
{
    // Never create a fast password hash in the request receipts.
    if (row is StaffInput input) row = new { input.Name, input.Role, input.Active, input.CanProvide, input.Username, PasswordProvided = !string.IsNullOrEmpty(input.Password) };
    return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row)));
}
static Dictionary<string, object?> RevisionRow(object row)
{
    var json = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var result = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    result["revision"] = Fingerprint(row); return result;
}
static void CheckRevision(HttpContext ctx, string current)
{
    var sent = ctx.Request.Headers.IfMatch.ToString();
    Must(!string.IsNullOrWhiteSpace(sent), "Atualize a página antes de salvar este registro.", 428);
    Must(sent == $"\"{current}\"", "Este registro mudou desde que você abriu a tela. Atualize e revise antes de salvar.", 409);
}
static string? RequestKey(HttpContext ctx)
{
    var key = ctx.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrEmpty(key)) return null;
    Must(Guid.TryParseExact(key, "D", out _), "Identificador de operação inválido.");
    return $"request:{ctx.Request.Path}:{key}";
}
static async Task<int?> TryReplay(HttpContext ctx, SalonDb db, object payload)
{
    var key = RequestKey(ctx); if (key is null) return null;
    var actor = Actor(ctx);
    var receipt = await db.Audits.AsNoTracking().FirstOrDefaultAsync(a => a.ActorId == actor && a.Action == key);
    if (receipt is null) return null;
    using var json = JsonDocument.Parse(receipt.Detail);
    Must(json.RootElement.GetProperty("fingerprint").GetString() == Fingerprint(payload), "Esta operação já foi salva com outros dados. Recarregue antes de iniciar um novo cadastro.", 409);
    var id = json.RootElement.GetProperty("id").GetInt32();
    if (payload is StaffInput { Password.Length: > 0 } staffInput)
    {
        var staff = await db.Staff.FindAsync(id);
        var hasher = ctx.RequestServices.GetRequiredService<IPasswordHasher<Staff>>();
        Must(staff?.PasswordHash != null && hasher.VerifyHashedPassword(staff, staff.PasswordHash, staffInput.Password) != PasswordVerificationResult.Failed, "Esta operação já foi salva com outra senha. Recarregue antes de iniciar um novo cadastro.", 409);
    }
    return id;
}
static void RecordReceipt(HttpContext ctx, SalonDb db, object payload, int id, string? fingerprint = null)
{
    var key = RequestKey(ctx); if (key is not null) Log(db, Actor(ctx), key, JsonSerializer.Serialize(new { id, fingerprint = fingerprint ?? Fingerprint(payload) }));
}
