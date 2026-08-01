// Canonical implementation; kept in one compilation unit to prevent duplicate merge artifacts.
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
    string DocumentPath(string eventId, string documentId);
}

public sealed class EventRepository : IEventRepository
{
    private readonly string root;
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<EventRecord>(stream, JsonOptions, ct);
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
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { if (Directory.Exists(EventDirectory(eventId))) Directory.Delete(EventDirectory(eventId), true); }
        finally { gate.Release(); }
    }

    public string DocumentPath(string eventId, string documentId)
    {
        ValidateId(eventId); ValidateId(documentId);
        return Path.Combine(EventDirectory(eventId), "documents", documentId + ".pdf");
    }

    private string EventDirectory(string id) => Path.Combine(root, id);
    private string EventFile(string id) => Path.Combine(EventDirectory(id), "event.json");
    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("不正なIDです。");
    }
}
