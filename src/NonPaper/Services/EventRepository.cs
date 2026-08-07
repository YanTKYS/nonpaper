using System.Collections.Concurrent;
using System.Text.Json;
using NonPaper.Models;

namespace NonPaper.Services;

public interface IEventRepository
{
    Task<EventRecord?> GetAsync(string eventId, CancellationToken ct = default);
    Task SaveAsync(EventRecord value, CancellationToken ct = default);
    Task<EventRecord> UpdateAsync(string eventId, Action<EventRecord> update, CancellationToken ct = default);
    Task<TResult> UpdateAsync<TResult>(string eventId, Func<EventRecord, TResult> update, CancellationToken ct = default);
    Task DeleteAsync(string eventId, CancellationToken ct = default);
    Task DeleteUpdatedAsync(string eventId, Action<EventRecord> beforeDelete, CancellationToken ct = default);
    string DocumentPath(string eventId, string documentId);
    string DocumentStagingPath(string eventId, string documentId);
    void CreateDocumentDirectory(string eventId);
}

public sealed class EventRepository : IEventRepository
{
    private readonly string root;
    // Entries deliberately live for the process lifetime. Removing an entry while a waiter still
    // holds its semaphore can create two independent locks for the same event.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public EventRepository(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Storage:Root"] ?? Path.Combine("data", "events");
        root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
    }

    public async Task<EventRecord?> GetAsync(string eventId, CancellationToken ct = default)
    {
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { return await ReadAsync(eventId, ct); }
        finally { gate.Release(); }
    }

    private async Task<EventRecord?> ReadAsync(string eventId, CancellationToken ct)
    {
        var path = EventFile(eventId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var value = await JsonSerializer.DeserializeAsync<EventRecord>(stream, JsonOptions, ct);
            ValidateRecord(value, eventId);
            return value;
        }
        catch (DataCorruptionException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new DataCorruptionException(eventId, ex);
        }
    }

    public async Task SaveAsync(EventRecord value, CancellationToken ct = default)
    {
        ValidateId(value.Id);
        var gate = Locks.GetOrAdd(value.Id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { await WriteAsync(value, ct); }
        finally { gate.Release(); }
    }

    private async Task WriteAsync(EventRecord value, CancellationToken ct)
    {
        var directory = EventDirectory(value.Id);
        Directory.CreateDirectory(Path.Combine(directory, "documents"));
        var target = EventFile(value.Id);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<EventRecord> UpdateAsync(string eventId, Action<EventRecord> update, CancellationToken ct = default)
    {
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var value = await ReadAsync(eventId, ct) ?? throw new KeyNotFoundException();
            update(value);
            await WriteAsync(value, ct);
            return value;
        }
        finally { gate.Release(); }
    }

    public async Task<TResult> UpdateAsync<TResult>(string eventId, Func<EventRecord, TResult> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var value = await ReadAsync(eventId, ct) ?? throw new KeyNotFoundException();
            var result = update(value);
            await WriteAsync(value, ct);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
    {
        await DeleteDirectoryCoreAsync(eventId, null, ct);
    }

    public async Task DeleteUpdatedAsync(string eventId, Action<EventRecord> beforeDelete, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(beforeDelete);
        await DeleteDirectoryCoreAsync(eventId, beforeDelete, ct);
    }

    private async Task DeleteDirectoryCoreAsync(string eventId, Action<EventRecord>? beforeDelete, CancellationToken ct)
    {
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 認証と「削除中」状態の保存を削除と同じロック内で行うことで、
            // 削除処理の途中に割り込んだ更新が削除済みイベントを復活させないようにする。
            if (beforeDelete is not null)
            {
                var value = await ReadAsync(eventId, ct) ?? throw new KeyNotFoundException();
                beforeDelete(value);
                await WriteAsync(value, ct);
            }
            var directory = EventDirectory(eventId);
            if (Directory.Exists(directory)) await DeleteDirectoryAsync(directory, ct);
        }
        finally { gate.Release(); }
    }

    // 参加者へのPDF配信中はファイルハンドルが開いており、Windowsでは一時的に削除できないことがある。
    // 数回の再試行でも解消しない場合は、時間をおいた再実行で解消できることが分かる例外へ変換する。
    private const int DeleteAttempts = 5;

    private static async Task DeleteDirectoryAsync(string directory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                    throw new StorageBusyException("資料ファイルが使用中のため削除できませんでした。しばらく待ってから、もう一度削除してください。", ex);
                await Task.Delay(200, ct);
            }
        }
    }

    public string DocumentPath(string eventId, string documentId)
    {
        ValidateId(eventId); ValidateId(documentId);
        return Path.Combine(EventDirectory(eventId), "documents", documentId + ".pdf");
    }

    /// <summary>アップロード中のPDFを書き込む一時ファイル。登録が確定した時点で <see cref="DocumentPath"/> へ移動する。</summary>
    public string DocumentStagingPath(string eventId, string documentId) => DocumentPath(eventId, documentId) + ".uploading";

    public void CreateDocumentDirectory(string eventId)
    {
        ValidateId(eventId);
        Directory.CreateDirectory(Path.Combine(EventDirectory(eventId), "documents"));
    }

    private string EventDirectory(string id) => Path.Combine(root, id);
    private string EventFile(string id) => Path.Combine(EventDirectory(id), "event.json");

    private static void ValidateRecord(EventRecord? value, string expectedId)
    {
        if (value is null || value.Id != expectedId || string.IsNullOrWhiteSpace(value.Title) ||
            value.ManagementTokenHash is null || value.ManagementTokenHash.Length != 64 ||
            value.ManagementTokenHash.Any(c => !char.IsAsciiHexDigit(c)) ||
            value.Status is not ("draft" or "published" or "closed" or "deleting") ||
            value.Documents is null || value.Documents.Any(d => string.IsNullOrWhiteSpace(d.Id) || d.Id.Length != 32 || d.Id.Any(c => !char.IsAsciiHexDigit(c))) ||
            value.Documents.Select(d => d.Id).Distinct().Count() != value.Documents.Count)
            throw new DataCorruptionException(expectedId);
    }
    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("不正なIDです。");
    }
}

public sealed class DataCorruptionException(string eventId, Exception? inner = null)
    : Exception($"Event data is corrupt: {eventId}", inner)
{
    public string EventId { get; } = eventId;
}
