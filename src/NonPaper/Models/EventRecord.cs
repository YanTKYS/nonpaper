namespace NonPaper.Models;

public sealed class EventRecord
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string ManagementTokenHash { get; set; }
    public List<DocumentRecord> Documents { get; set; } = [];
}

public sealed class DocumentRecord
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public int Order { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

public sealed record CreateEventRequest(string Title, string? Description, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record UpdateDocumentRequest(string Title);
public sealed record ReorderRequest(IReadOnlyList<string> DocumentIds);
