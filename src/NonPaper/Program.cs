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
app.MapPost("/api/events/{id}/status/{status}", async (string id, string status, HttpRequest req, EventService service, ILogger<Program> log, CancellationToken ct) => await Mutate(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    if (status is not ("draft" or "published" or "closed")) return Problem("状態が不正です。");
    var value = await service.UpdateAuthorizedAsync(id, Token(req), e => { e.Status = status; e.UpdatedAt = DateTimeOffset.Now; return e; }, ct);
    log.LogInformation("イベント {EventId} の状態を {Status} に変更しました", id, status);
    return Results.Ok(value);
}));

app.MapPost("/api/events/{id}/documents", async (string id, HttpRequest req, EventService service, IEventRepository repo, ILogger<Program> log, CancellationToken ct) => await Mutate(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    var files = (await req.ReadFormAsync(ct)).Files;
    if (files.Count == 0) return Problem("PDFを選択してください。");
    if (files.Count > service.MaxDocuments) return Problem("資料数が上限を超えています。", 413);
    var staged = new List<(string Path, string Id, string Name, long Size)>();
    var committed = new List<string>();
    try
    {
        foreach (var file in files)
        {
            if (file.Length == 0 || file.Length > service.MaxFileSize) return Problem("ファイルサイズが上限を超えています。", 413);
            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)) return Problem("PDF以外のファイルは登録できません。");
            var temp = Path.Combine(Path.GetTempPath(), "nonpaper-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var header = new byte[5];
                if (await input.ReadAsync(header, ct) != 5 || !header.SequenceEqual("%PDF-"u8.ToArray())) { File.Delete(temp); return Problem("PDF以外のファイルは登録できません。"); }
                await output.WriteAsync(header, ct); await input.CopyToAsync(output, ct);
            }
            staged.Add((temp, EventService.NewId(), Path.GetFileName(file.FileName), file.Length));
        }
        var result = await service.UpdateAuthorizedAsync(id, Token(req), e =>
        {
            if (e.Status != "draft") throw new InvalidOperationException("not-draft");
            if (e.Documents.Count + staged.Count > service.MaxDocuments) throw new InvalidOperationException("too-many");
            foreach (var item in staged)
            {
                var destination = repo.DocumentPath(id, item.Id); File.Move(item.Path, destination); committed.Add(destination);
                e.Documents.Add(new DocumentRecord { Id=item.Id, Title=Path.GetFileNameWithoutExtension(item.Name), OriginalFileName=item.Name, StoredFileName=item.Id+".pdf", Order=e.Documents.Count+1, FileSize=item.Size, UploadedAt=DateTimeOffset.Now });
            }
            e.UpdatedAt=DateTimeOffset.Now; return e.Documents.OrderBy(x=>x.Order).ToList();
        }, ct);
        foreach (var item in staged) log.LogInformation("イベント {EventId} に資料 {DocumentId} を登録しました", id, item.Id);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex) when (ex.Message == "not-draft") { return Problem("資料を変更できるのは下書き状態だけです。", 409); }
    catch (InvalidOperationException ex) when (ex.Message == "too-many") { return Problem("資料数が上限を超えています。", 413); }
    catch { foreach (var path in committed) File.Delete(path); throw; }
    finally { foreach (var item in staged) File.Delete(item.Path); }
})).DisableAntiforgery();

app.MapPut("/api/events/{id}/documents/{docId}", async (string id, string docId, UpdateDocumentRequest body, HttpRequest req, EventService service, CancellationToken ct) => await Mutate(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 200) return Problem("資料名を1～200文字で入力してください。");
    var document = await service.UpdateAuthorizedAsync(id, Token(req), e => { EnsureDraft(e); var d=e.Documents.SingleOrDefault(x=>x.Id==docId) ?? throw new KeyNotFoundException(); d.Title=body.Title.Trim(); e.UpdatedAt=DateTimeOffset.Now; return d; }, ct);
    return Results.Ok(document);
}));
app.MapPut("/api/events/{id}/documents/order", async (string id, ReorderRequest body, HttpRequest req, EventService service, CancellationToken ct) => await Mutate(async () =>
{
    if (!MutatingAllowed(req)) return Problem("不正な要求です。", 403);
    var documents=await service.UpdateAuthorizedAsync(id,Token(req),e=>{EnsureDraft(e);if(body.DocumentIds.Count!=e.Documents.Count||body.DocumentIds.Distinct().Count()!=body.DocumentIds.Count||body.DocumentIds.Any(x=>e.Documents.All(d=>d.Id!=x)))throw new ArgumentException();for(var i=0;i<body.DocumentIds.Count;i++)e.Documents.Single(d=>d.Id==body.DocumentIds[i]).Order=i+1;e.UpdatedAt=DateTimeOffset.Now;return e.Documents.OrderBy(x=>x.Order).ToList();},ct);
    return Results.Ok(documents);
}));
app.MapDelete("/api/events/{id}/documents/{docId}", async (string id,string docId,HttpRequest req,EventService service,IEventRepository repo,ILogger<Program> log,CancellationToken ct)=>await Mutate(async()=>
{
    if(!MutatingAllowed(req))return Problem("不正な要求です。",403);
    var path=repo.DocumentPath(id,docId);
    await service.UpdateAuthorizedAsync(id,Token(req),e=>{EnsureDraft(e);var d=e.Documents.SingleOrDefault(x=>x.Id==docId)??throw new KeyNotFoundException();e.Documents.Remove(d);var n=1;foreach(var x in e.Documents.OrderBy(x=>x.Order))x.Order=n++;e.UpdatedAt=DateTimeOffset.Now;return true;},ct);
    File.Delete(path);log.LogInformation("イベント {EventId} の資料 {DocumentId} を削除しました",id,docId);return Results.NoContent();
}));

app.MapGet("/api/events/{id}/documents/{docId}/content", async (string id,string docId,HttpResponse response,IEventRepository repo,CancellationToken ct)=>{ try { var e=await repo.GetAsync(id,ct); if(e is null||e.Status!="published")return Problem("この会議は公開されていません。",403); if(e.Documents.All(x=>x.Id!=docId))return Problem("資料が見つかりません。",404); var p=repo.DocumentPath(id,docId); if(!File.Exists(p))return Problem("資料が見つかりません。",404); response.Headers.CacheControl="no-store, private";response.Headers.Pragma="no-cache";return Results.File(p,"application/pdf",enableRangeProcessing:true,lastModified:null,entityTag:null); } catch(ArgumentException){return Problem("資料が見つかりません。",404);} });
app.MapDelete("/api/events/{id}", async(string id,HttpRequest req,EventService service,ILogger<Program> log,CancellationToken ct)=>await Mutate(async()=>{if(!MutatingAllowed(req))return Problem("不正な要求です。",403);await service.DeleteAuthorizedAsync(id,Token(req),ct);log.LogInformation("イベント {EventId} を削除しました",id);return Results.NoContent();}));
app.Map("/error",()=>Problem("処理中にエラーが発生しました。",500));
app.Lifetime.ApplicationStarted.Register(()=>app.Logger.LogInformation("NonPaper v0.1.0 を起動しました"));
app.Lifetime.ApplicationStopping.Register(()=>app.Logger.LogInformation("NonPaper を停止します"));
app.Run();
public partial class Program { }
