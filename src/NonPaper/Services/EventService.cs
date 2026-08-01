using System.Security.Cryptography;
using System.Text;
using NonPaper.Models;

namespace NonPaper.Services;

public sealed class EventService(IEventRepository repository, IConfiguration config, ILogger<EventService> logger)
{
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
        if (string.IsNullOrEmpty(title) || title.Length > 200) throw new ArgumentException("会議名を1～200文字で入力してください。");
        if (request.EndsAt <= request.StartsAt) throw new ArgumentException("終了日時は開催日時より後にしてください。");
        if ((request.Description?.Length ?? 0) > 4000) throw new ArgumentException("説明は4000文字以内で入力してください。");
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

    public Task<EventRecord> ChangeStatusAsync(string id, string? token, string next, CancellationToken ct) =>
        UpdateAuthorizedAsync(id, token, value =>
        {
            var error = EventStatusTransitions.Validate(value.Status, next);
            if (error is not null) throw new EventStateConflictException(error);
            value.Status = next;
            value.UpdatedAt = DateTimeOffset.Now;
        }, ct);

    public Task DeleteAuthorizedAsync(string id, string? token, CancellationToken ct) =>
        repository.DeleteAsync(id, value =>
        {
            if (!TokenMatches(value, token)) throw new UnauthorizedAccessException();
            value.Status = "deleting";
            value.UpdatedAt = DateTimeOffset.Now;
        }, ct);
}
