using Microsoft.AspNetCore.Http.Features;
using NonPaper.Models;
using NonPaper.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IEventRepository, EventRepository>();
builder.Services.AddSingleton<EventService>();

// 1回のアップロード要求は「1ファイル上限 × 最大件数」分の本文を受け付ける。
var maxFileSize = builder.Configuration.GetValue<long>("Upload:MaxFileSizeBytes", 104857600);
var maxDocuments = builder.Configuration.GetValue("Upload:MaxDocuments", 20);
var maxRequestBody = maxFileSize * Math.Max(1, maxDocuments) + 1048576;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxRequestBody);

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    ctx.Response.Headers.Append("Referrer-Policy", "no-referrer");
    await next();
});

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DataCorruptionException ex)
    {
        app.Logger.LogError(ex, "イベント {EventId} の永続データが破損しています", ex.EventId);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { message = "処理中にエラーが発生しました。" });
        }
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

static IResult Problem(string message, int status = 400) => Results.Json(new { message }, statusCode: status);

static string? Token(HttpRequest r) =>
    r.Headers.Authorization.ToString().StartsWith("Bearer ") ? r.Headers.Authorization.ToString()[7..] : null;

static bool MutatingAllowed(HttpRequest r) => r.Headers["X-NonPaper-Request"] == "1";

// 業務エラーだけを利用者向けの応答へ変換する。想定外の例外はそのまま送出し、500として扱う。
static IResult? Failure(Exception exception) => exception switch
{
    RequestValidationException e => Problem(e.Message),
    UnauthorizedAccessException => Problem("管理用URLが正しくありません。", 401),
    DocumentNotFoundException e => Problem(e.Message, 404),
    EventStateConflictException e => Problem(e.Message, 409),
    UploadLimitException e => Problem(e.Message, 413),
    ArgumentException or KeyNotFoundException => Problem("イベントが存在しないか、既に終了・削除されています。", 404),
    _ => null,
};

static async Task<IResult> Run(Func<Task<IResult>> handler)
{
    try { return await handler(); }
    catch (Exception ex)
    {
        var failure = Failure(ex);
        if (failure is null) throw;
        return failure;
    }
}

app.MapPost("/api/events", (CreateEventRequest request, EventService service, CancellationToken ct) => Run(async () =>
{
    var created = await service.CreateAsync(request, ct);
    return Results.Created($"/api/events/{created.Event.Id}", new { @event = created.Event, managementToken = created.Token });
}));

app.MapGet("/api/events/{id}/manage", (string id, HttpRequest req, EventService service, CancellationToken ct) => Run(async () =>
    Results.Ok(await service.AuthorizedAsync(id, Token(req), ct))));

app.MapGet("/api/events/{id}", (string id, IEventRepository repo, CancellationToken ct) => Run(async () =>
{
    var e = await repo.GetAsync(id, ct);
    if (e is null) return Problem("イベントが存在しないか、既に終了・削除されています。", 404);
    if (e.Status == "draft") return Problem("この会議は公開されていません。", 403);
    if (e.Status != "published") return Problem("この会議は終了しました。", 410);
    return Results.Ok(new
    {
        e.Id,
        e.Title,
        e.Description,
        e.StartsAt,
        e.EndsAt,
        e.Status,
        documents = e.Documents.OrderBy(d => d.Order).Select(d => new { d.Id, d.Title, d.Order }),
    });
}));

app.MapPost("/api/events/{id}/status/{status}", (string id, string status, HttpRequest req, EventService service, ILogger<Program> log, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    if (status is not ("draft" or "published" or "closed")) return Problem("この状態へは変更できません。", 409);
    var value = await service.ChangeStatusAsync(id, Token(req), status, ct);
    log.LogInformation("イベント {EventId} の状態を {Status} に変更しました", id, status);
    return Results.Ok(value);
}));

app.MapPost("/api/events/{id}/documents", (string id, HttpRequest req, EventService service, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);

    // サーバー（Kestrel/IIS）の本文サイズ上限は既定で30 MB前後であり、そのままでは
    // 設定した1ファイル上限（既定100 MiB）に届く前に要求が失敗する。この要求だけ上限を広げる。
    var sizeLimit = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeLimit is not null && !sizeLimit.IsReadOnly) sizeLimit.MaxRequestBodySize = maxRequestBody;

    IFormCollection form;
    try { form = await req.ReadFormAsync(ct); }
    catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
    {
        return Problem("一度にアップロードできる容量を超えています。ファイルを分けて登録してください。", 413);
    }

    return Results.Ok(await service.AddDocumentsAsync(id, Token(req), form.Files, ct));
})).DisableAntiforgery();

app.MapPut("/api/events/{id}/documents/{docId}", (string id, string docId, UpdateDocumentRequest body, HttpRequest req, EventService service, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    return Results.Ok(await service.RenameDocumentAsync(id, Token(req), docId, body.Title, ct));
}));

app.MapPut("/api/events/{id}/documents/order", (string id, ReorderRequest body, HttpRequest req, EventService service, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    return Results.Ok(await service.ReorderDocumentsAsync(id, Token(req), body.DocumentIds, ct));
}));

app.MapDelete("/api/events/{id}/documents/{docId}", (string id, string docId, HttpRequest req, EventService service, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    await service.RemoveDocumentAsync(id, Token(req), docId, ct);
    return Results.NoContent();
}));

app.MapGet("/api/events/{id}/documents/{docId}/content", (string id, string docId, HttpResponse response, IEventRepository repo, CancellationToken ct) => Run(async () =>
{
    var e = await repo.GetAsync(id, ct);
    if (e is null || e.Status != "published") return Problem("この会議は公開されていません。", 403);
    if (e.Documents.All(x => x.Id != docId)) return Problem("資料が見つかりません。", 404);

    var p = repo.DocumentPath(id, docId);
    if (!File.Exists(p)) return Problem("資料が見つかりません。", 404);

    response.Headers.CacheControl = "no-store, private";
    response.Headers.Pragma = "no-cache";
    return Results.File(p, "application/pdf", enableRangeProcessing: true, lastModified: null, entityTag: null);
}));

app.MapDelete("/api/events/{id}", (string id, HttpRequest req, EventService service, ILogger<Program> log, CancellationToken ct) => Run(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    await service.DeleteAuthorizedAsync(id, Token(req), ct);
    log.LogInformation("イベント {EventId} を削除しました", id);
    return Results.NoContent();
}));

app.Map("/error", () => Problem("処理中にエラーが発生しました。", 500));
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("NonPaper v0.1.1 を起動しました"));
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("NonPaper を停止します"));
app.Run();

public partial class Program { }
