using Microsoft.AspNetCore.Http.Features;
using NonPaper.Models;
using NonPaper.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IEventRepository, EventRepository>();
builder.Services.AddSingleton<EventService>();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = builder.Configuration.GetValue<long>("Upload:MaxFileSizeBytes", 104857600) * builder.Configuration.GetValue("Upload:MaxDocuments", 20));
var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");
app.Use(async (ctx, next) => { ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff"); ctx.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN"); ctx.Response.Headers.Append("Referrer-Policy", "no-referrer"); await next(); });
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DataCorruptionException ex)
    {
        app.Logger.LogError(ex, "イベント {EventId} の永続データが破損しています", ex.EventId);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { message = "処理中にエラーが発生しました。" }); }
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();

static IResult Problem(string message, int status = 400) => Results.Json(new { message }, statusCode: status);
static string? Token(HttpRequest r) => r.Headers.Authorization.ToString().StartsWith("Bearer ") ? r.Headers.Authorization.ToString()[7..] : null;
static bool MutatingAllowed(HttpRequest r) => r.Headers["X-NonPaper-Request"] == "1";
static async Task<(EventRecord? value, IResult? error)> Auth(string id, HttpRequest req, EventService service, CancellationToken ct)
{
    try { return (await service.AuthorizedAsync(id, Token(req), ct), null); }
    catch (ArgumentException) { return (null, Problem("イベントが存在しないか、既に終了・削除されています。", 404)); }
    catch (KeyNotFoundException) { return (null, Problem("イベントが存在しないか、既に終了・削除されています。", 404)); }
    catch (UnauthorizedAccessException) { return (null, Problem("管理用URLが正しくありません。", 401)); }
}

static void EnsureDraft(EventRecord e) { if (e.Status != "draft") throw new InvalidOperationException("not-draft"); }

static async Task<IResult> Mutate(Func<Task<IResult>> operation)
{
    try { return await operation(); }
    catch (ArgumentException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (KeyNotFoundException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (UnauthorizedAccessException) { return Problem("管理用URLが正しくありません。", 401); }
    catch (InvalidOperationException ex) when (ex.Message == "not-draft") { return Problem("資料を変更できるのは下書き状態だけです。", 409); }
}

app.MapPost("/api/events", async (CreateEventRequest request, EventService service, CancellationToken ct) => { try { var x = await service.CreateAsync(request, ct); return Results.Created($"/api/events/{x.Event.Id}", new { @event = x.Event, managementToken = x.Token }); } catch (ArgumentException ex) { return Problem(ex.Message); } });
app.MapGet("/api/events/{id}/manage", async (string id, HttpRequest req, EventService service, CancellationToken ct) => { var a = await Auth(id, req, service, ct); return a.error ?? Results.Ok(a.value); });
app.MapGet("/api/events/{id}", async (string id, IEventRepository repo, CancellationToken ct) => { try { var e = await repo.GetAsync(id, ct); if (e is null) return Problem("イベントが存在しないか、既に終了・削除されています。", 404); if (e.Status == "draft") return Problem("この会議は公開されていません。", 403); if (e.Status != "published") return Problem("この会議は終了しました。", 410); return Results.Ok(new { e.Id, e.Title, e.Description, e.StartsAt, e.EndsAt, e.Status, documents = e.Documents.OrderBy(d => d.Order).Select(d => new { d.Id, d.Title, d.Order }) }); } catch (ArgumentException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); } });
app.MapPost("/api/events/{id}/status/{status}", async (string id, string status, HttpRequest req, EventService service, ILogger<Program> log, CancellationToken ct) =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    if (status is not ("draft" or "published" or "closed")) return Problem("この状態へは変更できません。", 409);
    try
    {
        var value = await service.ChangeStatusAsync(id, Token(req), status, ct);
        log.LogInformation("イベント {EventId} の状態を {Status} に変更しました", id, status);
        return Results.Ok(value);
    }
    catch (ArgumentException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (KeyNotFoundException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (UnauthorizedAccessException) { return Problem("管理用URLが正しくありません。", 401); }
    catch (EventStateConflictException ex) { return Problem(ex.Message, 409); }
});
app.MapPost("/api/events/{id}/documents", async (string id, HttpRequest req, EventService service, IEventRepository repo, ILogger<Program> log, CancellationToken ct) => { if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403); var a = await Auth(id, req, service, ct); if (a.error is not null) return a.error; if (a.value!.Status != "draft") return Problem("資料を変更できるのは下書き状態だけです。", 409); var files = req.Form.Files; if (files.Count == 0) return Problem("PDFを選択してください。"); if (a.value.Documents.Count + files.Count > service.MaxDocuments) return Problem("資料数が上限を超えています。", 413); foreach (var file in files) { if (file.Length == 0 || file.Length > service.MaxFileSize) return Problem("ファイルサイズが上限を超えています。", 413); if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)) return Problem("PDF以外のファイルは登録できません。"); await using var input = file.OpenReadStream(); var header = new byte[5]; if (await input.ReadAsync(header, ct) != 5 || !header.SequenceEqual("%PDF-"u8.ToArray())) return Problem("PDF以外のファイルは登録できません。"); var docId = EventService.NewId(); var path = repo.DocumentPath(id, docId); await using var output = new FileStream(path, FileMode.CreateNew); await output.WriteAsync(header, ct); await input.CopyToAsync(output, ct); var safeName = Path.GetFileName(file.FileName); a.value.Documents.Add(new DocumentRecord { Id = docId, Title = Path.GetFileNameWithoutExtension(safeName), OriginalFileName = safeName, StoredFileName = docId + ".pdf", Order = a.value.Documents.Count + 1, FileSize = file.Length, UploadedAt = DateTimeOffset.Now }); log.LogInformation("イベント {EventId} に資料 {DocumentId} を登録しました", id, docId); } a.value.UpdatedAt = DateTimeOffset.Now; await repo.SaveAsync(a.value, ct); return Results.Ok(a.value.Documents.OrderBy(x => x.Order)); }).DisableAntiforgery();
app.MapPut("/api/events/{id}/documents/{docId}", async (string id, string docId, UpdateDocumentRequest body, HttpRequest req, EventService service, IEventRepository repo, CancellationToken ct) => { if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403); var a = await Auth(id, req, service, ct); if (a.error is not null) return a.error; if (a.value!.Status != "draft") return Problem("資料を変更できるのは下書き状態だけです。", 409); var d = a.value.Documents.SingleOrDefault(x => x.Id == docId); if (d is null) return Problem("資料が見つかりません。", 404); if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 200) return Problem("資料名を1～200文字で入力してください。"); d.Title = body.Title.Trim(); a.value.UpdatedAt = DateTimeOffset.Now; await repo.SaveAsync(a.value, ct); return Results.Ok(d); });
app.MapPut("/api/events/{id}/documents/order", async (string id, ReorderRequest body, HttpRequest req, EventService service, IEventRepository repo, CancellationToken ct) => { if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403); var a = await Auth(id, req, service, ct); if (a.error is not null) return a.error; if (a.value!.Status != "draft") return Problem("資料を変更できるのは下書き状態だけです。", 409); if (body.DocumentIds.Count != a.value.Documents.Count || body.DocumentIds.Distinct().Count() != body.DocumentIds.Count || body.DocumentIds.Any(x => a.value.Documents.All(d => d.Id != x))) return Problem("資料の並び順が不正です。"); for (var i=0;i<body.DocumentIds.Count;i++) a.value.Documents.Single(d => d.Id == body.DocumentIds[i]).Order=i+1; await repo.SaveAsync(a.value, ct); return Results.Ok(a.value.Documents.OrderBy(x=>x.Order)); });
app.MapDelete("/api/events/{id}/documents/{docId}", async (string id, string docId, HttpRequest req, EventService service, IEventRepository repo, ILogger<Program> log, CancellationToken ct) => { if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403); var a=await Auth(id,req,service,ct); if(a.error is not null)return a.error;if(a.value!.Status!="draft")return Problem("資料を変更できるのは下書き状態だけです。",409); var d=a.value.Documents.SingleOrDefault(x=>x.Id==docId); if(d is null)return Problem("資料が見つかりません。",404); File.Delete(repo.DocumentPath(id,docId)); a.value.Documents.Remove(d); var n=1; foreach(var x in a.value.Documents.OrderBy(x=>x.Order))x.Order=n++; await repo.SaveAsync(a.value,ct); log.LogInformation("イベント {EventId} の資料 {DocumentId} を削除しました",id,docId); return Results.NoContent(); });
app.MapGet("/api/events/{id}/documents/{docId}/content", async (string id,string docId,HttpResponse response,IEventRepository repo,CancellationToken ct)=>{ try { var e=await repo.GetAsync(id,ct); if(e is null||e.Status!="published")return Problem("この会議は公開されていません。",403); if(e.Documents.All(x=>x.Id!=docId))return Problem("資料が見つかりません。",404); var p=repo.DocumentPath(id,docId); if(!File.Exists(p))return Problem("資料が見つかりません。",404); response.Headers.CacheControl="no-store, private";response.Headers.Pragma="no-cache";return Results.File(p,"application/pdf",enableRangeProcessing:true,lastModified:null,entityTag:null); } catch(ArgumentException){return Problem("資料が見つかりません。",404);} });
app.MapDelete("/api/events/{id}", async (string id, HttpRequest req, EventService service, ILogger<Program> log, CancellationToken ct) =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    try
    {
        await service.DeleteAuthorizedAsync(id, Token(req), ct);
        log.LogInformation("イベント {EventId} を削除しました", id);
        return Results.NoContent();
    }
    catch (ArgumentException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (KeyNotFoundException) { return Problem("イベントが存在しないか、既に終了・削除されています。", 404); }
    catch (UnauthorizedAccessException) { return Problem("管理用URLが正しくありません。", 401); }
});
app.Map("/error",()=>Problem("処理中にエラーが発生しました。",500));
app.Lifetime.ApplicationStarted.Register(()=>app.Logger.LogInformation("NonPaper v0.1.0 を起動しました"));
app.Lifetime.ApplicationStopping.Register(()=>app.Logger.LogInformation("NonPaper を停止します"));
app.Run();
public partial class Program { }
