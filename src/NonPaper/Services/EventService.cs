using System.Security.Cryptography;
using System.Text;
using NonPaper.Models;

namespace NonPaper.Services;

public sealed class EventService(IEventRepository repository, IConfiguration config, ILogger<EventService> logger)
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    public int MaxDocuments => config.GetValue("Upload:MaxDocuments", 20);
    public long MaxFileSize => config.GetValue<long>("Upload:MaxFileSizeBytes", 104857600);
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public static bool TokenMatches(EventRecord e, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (e.ManagementTokenHash?.Length != 64 || e.ManagementTokenHash.Any(c => !char.IsAsciiHexDigit(c))) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(e.ManagementTokenHash), Convert.FromHexString(HashToken(token)));
    }

    public async Task<(EventRecord Event, string Token)> CreateAsync(CreateEventRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200) throw new RequestValidationException("会議名を1～200文字で入力してください。");
        if (request.EndsAt <= request.StartsAt) throw new RequestValidationException("終了日時は開催日時より後にしてください。");
        if ((request.Description?.Length ?? 0) > 4000) throw new RequestValidationException("説明は4000文字以内で入力してください。");
        var token = NewToken(); var now = DateTimeOffset.Now;
        var e = new EventRecord { Id = NewId(), Title = title, Description = request.Description?.Trim() ?? "", StartsAt = request.StartsAt, EndsAt = request.EndsAt, CreatedAt = now, UpdatedAt = now, ManagementTokenHash = HashToken(token) };
        await repository.SaveAsync(e, ct); logger.LogInformation("イベント {EventId} を作成しました", e.Id);
        return (e, token);
    }

    public async Task<EventRecord> AuthorizedAsync(string id, string? token, CancellationToken ct)
    {
        var e = await repository.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        if (!TokenMatches(e, token)) throw new UnauthorizedAccessException();
        return e;
    }

    public Task<EventRecord> UpdateAuthorizedAsync(string id, string? token, Action<EventRecord> update, CancellationToken ct) =>
        repository.UpdateAsync(id, value =>
        {
            if (!TokenMatches(value, token)) throw new UnauthorizedAccessException();
            update(value);
        }, ct);

    /// <summary>認証から保存までをイベント単位のロック内で行い、同時更新による上書きを防ぐ。</summary>
    public Task<TResult> UpdateAuthorizedAsync<TResult>(string id, string? token, Func<EventRecord, TResult> update, CancellationToken ct) =>
        repository.UpdateAsync(id, value =>
        {
            if (!TokenMatches(value, token)) throw new UnauthorizedAccessException();
            return update(value);
        }, ct);

    public Task<EventRecord> ChangeStatusAsync(string id, string? token, string next, CancellationToken ct) =>
        UpdateAuthorizedAsync(id, token, value =>
        {
            var error = EventStatusTransitions.Validate(value.Status, next);
            if (error is not null) throw new EventStateConflictException(error);
            value.Status = next;
            value.UpdatedAt = DateTimeOffset.Now;
        }, ct);

    public async Task DeleteAuthorizedAsync(string id, string? token, CancellationToken ct)
    {
        // 認証、「削除中」の保存、実体の削除を同一ロック内で実行する。
        await repository.DeleteUpdatedAsync(id, value =>
        {
            if (!TokenMatches(value, token)) throw new UnauthorizedAccessException();
            value.Status = "deleting";
            value.UpdatedAt = DateTimeOffset.Now;
        }, ct);
    }

    /// <summary>資料の変更（登録・名称変更・並び替え・削除）に共通する認証と状態検証をロック内で行う。</summary>
    public Task<TResult> EditDocumentsAsync<TResult>(string id, string? token, Func<EventRecord, TResult> edit, CancellationToken ct) =>
        UpdateAuthorizedAsync(id, token, value =>
        {
            RequireDocumentsEditable(value);
            var result = edit(value);
            value.UpdatedAt = DateTimeOffset.Now;
            return result;
        }, ct);

    public async Task<List<DocumentRecord>> AddDocumentsAsync(string id, string? token, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        // 本文を保存する前に、認証・状態・全ファイルの妥当性を確認する。
        // 1件でも登録できないファイルがあれば、どのファイルも登録しない。
        var current = await AuthorizedAsync(id, token, ct);
        RequireDocumentsEditable(current);
        if (files.Count == 0) throw new RequestValidationException("PDFを選択してください。");
        RequireDocumentCount(current.Documents.Count + files.Count);
        foreach (var file in files)
        {
            if (file.Length <= 0) throw new RequestValidationException($"「{FileNameOf(file)}」は空のファイルです。");
            if (file.Length > MaxFileSize) throw new UploadLimitException($"「{FileNameOf(file)}」のサイズが上限（{MaxFileSize / 1048576} MiB）を超えています。");
            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new RequestValidationException($"「{FileNameOf(file)}」はPDFではありません。PDFファイルだけを登録できます。");
        }

        var staged = new List<(DocumentRecord Document, string Staging, string Final)>();
        var committed = false;
        try
        {
            repository.CreateDocumentDirectory(id);
            foreach (var file in files)
            {
                var documentId = NewId();
                var staging = repository.DocumentStagingPath(id, documentId);
                await StageAsync(file, staging, ct);
                var name = FileNameOf(file);
                staged.Add((new DocumentRecord
                {
                    Id = documentId,
                    Title = Path.GetFileNameWithoutExtension(name),
                    OriginalFileName = name,
                    StoredFileName = documentId + ".pdf",
                    FileSize = file.Length,
                    UploadedAt = DateTimeOffset.Now,
                }, staging, repository.DocumentPath(id, documentId)));
            }

            var documents = await EditDocumentsAsync(id, token, value =>
            {
                RequireDocumentCount(value.Documents.Count + staged.Count);
                var order = value.Documents.Count == 0 ? 0 : value.Documents.Max(d => d.Order);
                foreach (var item in staged)
                {
                    File.Move(item.Staging, item.Final, true);
                    item.Document.Order = ++order;
                    value.Documents.Add(item.Document);
                }
                return Sorted(value);
            }, ct);

            committed = true;
            foreach (var item in staged) logger.LogInformation("イベント {EventId} に資料 {DocumentId} を登録しました", id, item.Document.Id);
            return documents;
        }
        finally
        {
            // 途中で失敗した場合は、今回作成したファイルを残さない。
            foreach (var item in staged)
            {
                TryDeleteFile(item.Staging);
                if (!committed) TryDeleteFile(item.Final);
            }
        }
    }

    public Task<DocumentRecord> RenameDocumentAsync(string id, string? token, string documentId, string? title, CancellationToken ct)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200) throw new RequestValidationException("資料名を1～200文字で入力してください。");
        return EditDocumentsAsync(id, token, value =>
        {
            var document = FindDocument(value, documentId);
            document.Title = trimmed;
            return document;
        }, ct);
    }

    public Task<List<DocumentRecord>> ReorderDocumentsAsync(string id, string? token, IReadOnlyList<string>? documentIds, CancellationToken ct)
    {
        if (documentIds is null) throw new RequestValidationException("資料の並び順が不正です。");
        return EditDocumentsAsync(id, token, value =>
        {
            if (documentIds.Count != value.Documents.Count
                || documentIds.Distinct().Count() != documentIds.Count
                || documentIds.Any(x => value.Documents.All(d => d.Id != x)))
                throw new RequestValidationException("資料の並び順が不正です。");
            for (var i = 0; i < documentIds.Count; i++) FindDocument(value, documentIds[i]).Order = i + 1;
            return Sorted(value);
        }, ct);
    }

    public async Task RemoveDocumentAsync(string id, string? token, string documentId, CancellationToken ct)
    {
        await EditDocumentsAsync(id, token, value =>
        {
            var document = FindDocument(value, documentId);
            value.Documents.Remove(document);
            Renumber(value);
            return document;
        }, ct);
        // JSONの保存に成功してからPDFを削除する。削除できなくても登録済み一覧との不整合は生じない。
        TryDeleteFile(repository.DocumentPath(id, documentId));
        logger.LogInformation("イベント {EventId} の資料 {DocumentId} を削除しました", id, documentId);
    }

    private async Task StageAsync(IFormFile file, string path, CancellationToken ct)
    {
        await using var input = file.OpenReadStream();
        var header = new byte[PdfSignature.Length];
        var read = await input.ReadAtLeastAsync(header, header.Length, false, ct);
        if (read != header.Length || !header.SequenceEqual(PdfSignature))
            throw new RequestValidationException($"「{FileNameOf(file)}」はPDFではありません。PDFファイルだけを登録できます。");

        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await output.WriteAsync(header, ct);
        await input.CopyToAsync(output, ct);
    }

    private void RequireDocumentCount(int count)
    {
        if (count > MaxDocuments) throw new UploadLimitException($"1つのイベントに登録できる資料は{MaxDocuments}件までです。");
    }

    private static void RequireDocumentsEditable(EventRecord value)
    {
        if (value.Status == "deleting") throw new KeyNotFoundException();
        if (value.Status != "draft") throw new EventStateConflictException("資料を変更できるのは下書き状態だけです。");
    }

    private static DocumentRecord FindDocument(EventRecord value, string documentId) =>
        value.Documents.FirstOrDefault(x => x.Id == documentId) ?? throw new DocumentNotFoundException();

    private static List<DocumentRecord> Sorted(EventRecord value) => value.Documents.OrderBy(d => d.Order).ToList();

    private static void Renumber(EventRecord value)
    {
        var order = 1;
        foreach (var document in value.Documents.OrderBy(d => d.Order)) document.Order = order++;
    }

    // 外部から渡されたファイル名はパスの組み立てに使用せず、表示と記録にだけ用いる。
    private static string FileNameOf(IFormFile file) => Path.GetFileName(file.FileName);

    private void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "PDFファイルを削除できませんでした: {Path}", path);
        }
    }
}
