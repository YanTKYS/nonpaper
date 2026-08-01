using System.Collections.Concurrent;
using System.Text.Json;
using NonPaper.Models;

namespace NonPaper.Services;

public interface IEventRepository
{
    Task<EventRecord?> GetAsync(string eventId, CancellationToken ct = default);
    Task SaveAsync(EventRecord value, CancellationToken ct = default);
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
        try
        {
            var directory = EventDirectory(value.Id);
            Directory.CreateDirectory(Path.Combine(directory, "documents"));
            var target = EventFile(value.Id);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, target, true);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
    {
        ValidateId(eventId);
        var gate = Locks.GetOrAdd(eventId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { if (Directory.Exists(EventDirectory(eventId))) Directory.Delete(EventDirectory(eventId), true); }
        finally { gate.Release(); Locks.TryRemove(eventId, out _); }
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
