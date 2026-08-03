using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NonPaper.Models;
using NonPaper.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NonPaper.Tests;

public sealed class EventRepositoryTests : IDisposable
{
    private const string Token = "test";
    private readonly string root = Path.Combine(Path.GetTempPath(), "nonpaper-tests-" + Guid.NewGuid());
    private EventRepository Repository => new(new Environment(root), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:Root"] = root }).Build());

    [Fact] public async Task EventJson_IsSavedAndLoaded()
    {
        var repository = Repository; var value = Event();
        await repository.SaveAsync(value); var loaded = await repository.GetAsync(value.Id);
        Assert.Equal(value.Title, loaded!.Title); Assert.True(File.Exists(Path.Combine(root, value.Id, "event.json")));
    }

    [Fact] public async Task ConcurrentSafeUpdates_AlwaysLeaveValidJson()
    {
        var repository = Repository; var value = Event();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i => { var copy = Event(); copy.Id=value.Id; copy.Title=$"会議{i}"; await repository.SaveAsync(copy); }));
        Assert.NotNull(await repository.GetAsync(value.Id));
    }

    [Fact]
    public async Task UpdateAsync_CanReturnAValueFromTheLockedMutation()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);

        var title = await repository.UpdateAsync(value.Id, current =>
        {
            current.Title = "更新後の会議";
            return current.Title;
        });

        Assert.Equal("更新後の会議", title);
        Assert.Equal(title, (await repository.GetAsync(value.Id))!.Title);
    }

    [Theory] [InlineData("../bad")] [InlineData("0000000000000000000000000000000g")] [InlineData("")]
    public void InvalidAndTraversalIds_AreRejected(string id) => Assert.Throws<ArgumentException>(() => Repository.DocumentPath(id, EventService.NewId()));

    [Fact] public async Task Delete_RemovesJsonPdfAndFolder()
    {
        var repository=Repository;var value=Event();await repository.SaveAsync(value);await File.WriteAllTextAsync(repository.DocumentPath(value.Id,EventService.NewId()),"pdf");await repository.DeleteAsync(value.Id);Assert.False(Directory.Exists(Path.Combine(root,value.Id)));
    }

    [Fact] public void ManagementToken_UsesHashAndConstantTimeComparison()
    {
        var token=EventService.NewToken();var value=Event();value.ManagementTokenHash=EventService.HashToken(token);
        Assert.True(EventService.TokenMatches(value,token));Assert.False(EventService.TokenMatches(value,token+"x"));Assert.DoesNotContain(token,value.ManagementTokenHash);
    }

    [Theory]
    [InlineData("draft", "published")]
    [InlineData("draft", "closed")]
    [InlineData("published", "draft")]
    [InlineData("published", "closed")]
    public void AllowedStatusTransitions_AreAccepted(string current, string next) =>
        Assert.True(EventStatusTransitions.CanTransition(current, next));

    [Theory]
    [InlineData("closed", "draft", "終了した会議の状態は変更できません。")]
    [InlineData("closed", "published", "終了した会議の状態は変更できません。")]
    [InlineData("draft", "draft", "会議は既に指定された状態です。")]
    [InlineData("published", "published", "会議は既に指定された状態です。")]
    [InlineData("closed", "closed", "会議は既に指定された状態です。")]
    [InlineData("draft", "invalid", "この状態へは変更できません。")]
    [InlineData("draft", "deleting", "この状態へは変更できません。")]
    public void RejectedStatusTransitions_ReturnUserMessage(string current, string next, string message) =>
        Assert.Equal(message, EventStatusTransitions.Validate(current, next));

    [Fact]
    public async Task RejectedStatusTransition_DoesNotChangeJsonOrUpdatedAt()
    {
        var repository = Repository;
        var value = Event();
        var token = "management-token";
        value.ManagementTokenHash = EventService.HashToken(token);
        await repository.SaveAsync(value);
        var beforeJson = await File.ReadAllTextAsync(Path.Combine(root, value.Id, "event.json"));
        var service = Service(repository);

        await Assert.ThrowsAsync<EventStateConflictException>(() => service.ChangeStatusAsync(value.Id, token, "draft", default));

        var loaded = await repository.GetAsync(value.Id);
        Assert.Equal(value.UpdatedAt, loaded!.UpdatedAt);
        Assert.Equal(beforeJson, await File.ReadAllTextAsync(Path.Combine(root, value.Id, "event.json")));
    }

    [Fact]
    public async Task ConcurrentStatusChanges_ValidateLatestStateInsideLock()
    {
        var repository = Repository;
        var value = Event();
        var token = "management-token";
        value.ManagementTokenHash = EventService.HashToken(token);
        await repository.SaveAsync(value);
        var service = Service(repository);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await service.ChangeStatusAsync(value.Id, token, "published", default); return true; }
            catch (EventStateConflictException) { return false; }
        }));

        Assert.Single(results, result => result);
        Assert.Equal("published", (await repository.GetAsync(value.Id))!.Status);
    }

    [Fact]
    public async Task ClosedEvent_CanBeDeletedByAuthorizedManager()
    {
        var repository = Repository;
        var value = Event();
        var token = "management-token";
        value.Status = "closed";
        value.ManagementTokenHash = EventService.HashToken(token);
        await repository.SaveAsync(value);

        await Service(repository).DeleteAuthorizedAsync(value.Id, token, default);

        Assert.False(Directory.Exists(Path.Combine(root, value.Id)));
    }

    [Fact]
    public async Task DeleteAuthorizedAsync_RejectsInvalidTokenWithoutDeletingEvent()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(repository).DeleteAuthorizedAsync(value.Id, "invalid-token", default));

        Assert.NotNull(await repository.GetAsync(value.Id));
    }

    [Fact]
    public async Task ConcurrentDocumentRenames_KeepEveryAcceptedChange()
    {
        var repository = Repository;
        var value = Event();
        value.Documents.Add(Document(1));
        value.Documents.Add(Document(2));
        await repository.SaveAsync(value);
        var service = Service(repository);

        await Task.WhenAll(
            service.RenameDocumentAsync(value.Id, Token, value.Documents[0].Id, "資料A", default),
            service.RenameDocumentAsync(value.Id, Token, value.Documents[1].Id, "資料B", default));

        var loaded = await repository.GetAsync(value.Id);
        Assert.Equal(new[] { "資料A", "資料B" }, loaded!.Documents.OrderBy(d => d.Order).Select(d => d.Title));
    }

    [Fact]
    public async Task Upload_RegistersDocumentsInOrderAndStoresFiles()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);
        var service = Service(repository);

        var documents = await service.AddDocumentsAsync(
            value.Id, Token, new IFormFile[] { Pdf("会議次第.pdf"), Pdf("参考資料.pdf") }, default);

        Assert.Equal(new[] { "会議次第", "参考資料" }, documents.Select(d => d.Title));
        Assert.Equal(new[] { 1, 2 }, documents.Select(d => d.Order));
        Assert.All(documents, d => Assert.True(System.IO.File.Exists(repository.DocumentPath(value.Id, d.Id))));
        Assert.Equal(documents.Select(d => d.Id), (await repository.GetAsync(value.Id))!.Documents.Select(d => d.Id));
        Assert.Empty(Directory.GetFiles(DocumentDirectory(value.Id), "*.uploading"));
    }

    [Fact]
    public async Task UploadContainingNonPdf_RegistersNothingAndLeavesNoFile()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);
        var service = Service(repository);

        await Assert.ThrowsAsync<RequestValidationException>(() => service.AddDocumentsAsync(
            value.Id, Token, new IFormFile[] { Pdf("正しい資料.pdf"), Upload("画像.pdf", "not a pdf"u8.ToArray()) }, default));

        Assert.Empty((await repository.GetAsync(value.Id))!.Documents);
        Assert.Empty(Directory.GetFiles(DocumentDirectory(value.Id)));
    }

    [Fact]
    public async Task DeletingDocument_RemovesFileAndRenumbersRemainingOrder()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);
        var service = Service(repository);
        var documents = await service.AddDocumentsAsync(
            value.Id, Token, new IFormFile[] { Pdf("1.pdf"), Pdf("2.pdf"), Pdf("3.pdf") }, default);
        var removed = documents[1];

        await service.RemoveDocumentAsync(value.Id, Token, removed.Id, default);

        var loaded = await repository.GetAsync(value.Id);
        Assert.Equal(new[] { "1", "3" }, loaded!.Documents.OrderBy(d => d.Order).Select(d => d.Title));
        Assert.Equal(new[] { 1, 2 }, loaded.Documents.OrderBy(d => d.Order).Select(d => d.Order));
        Assert.False(System.IO.File.Exists(repository.DocumentPath(value.Id, removed.Id)));
    }

    [Fact]
    public async Task PublishedEvent_RejectsEveryDocumentChange()
    {
        var repository = Repository;
        var value = Event();
        value.Status = "published";
        value.Documents.Add(Document(1));
        await repository.SaveAsync(value);
        var service = Service(repository);
        var documentId = value.Documents[0].Id;

        await Assert.ThrowsAsync<EventStateConflictException>(() => service.RenameDocumentAsync(value.Id, Token, documentId, "新しい名前", default));
        await Assert.ThrowsAsync<EventStateConflictException>(() => service.ReorderDocumentsAsync(value.Id, Token, new[] { documentId }, default));
        await Assert.ThrowsAsync<EventStateConflictException>(() => service.RemoveDocumentAsync(value.Id, Token, documentId, default));
        await Assert.ThrowsAsync<EventStateConflictException>(() => service.AddDocumentsAsync(value.Id, Token, new IFormFile[] { Pdf("追加.pdf") }, default));

        var loaded = await repository.GetAsync(value.Id);
        Assert.Equal("資料1", Assert.Single(loaded!.Documents).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankDocumentTitle_IsRejected(string title)
    {
        var repository = Repository;
        var value = Event();
        value.Documents.Add(Document(1));
        await repository.SaveAsync(value);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(repository).RenameDocumentAsync(value.Id, Token, value.Documents[0].Id, title, default));
    }

    [Fact]
    public async Task Reorder_RejectsListThatDoesNotMatchRegisteredDocuments()
    {
        var repository = Repository;
        var value = Event();
        value.Documents.Add(Document(1));
        value.Documents.Add(Document(2));
        await repository.SaveAsync(value);
        var service = Service(repository);
        var first = value.Documents[0].Id;

        await Assert.ThrowsAsync<RequestValidationException>(() => service.ReorderDocumentsAsync(value.Id, Token, new[] { first }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.ReorderDocumentsAsync(value.Id, Token, new[] { first, first }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.ReorderDocumentsAsync(value.Id, Token, null, default));
    }

    private string DocumentDirectory(string eventId) => Path.Combine(root, eventId, "documents");
    private static IFormFile Pdf(string name) => Upload(name, Encoding.UTF8.GetBytes("%PDF-1.7\n" + name));
    private static IFormFile Upload(string name, byte[] content) => new FormFile(new MemoryStream(content), 0, content.Length, "files", name);

    private static EventService Service(IEventRepository repository) => new(
        repository,
        new ConfigurationBuilder().Build(),
        NullLogger<EventService>.Instance);

    private static EventRecord Event()=>new(){Id=EventService.NewId(),Title="庁内会議",StartsAt=DateTimeOffset.Now,EndsAt=DateTimeOffset.Now.AddHours(1),CreatedAt=DateTimeOffset.Now,UpdatedAt=DateTimeOffset.Now,ManagementTokenHash=EventService.HashToken(Token)};
    private static DocumentRecord Document(int number) => new() { Id = number.ToString("x32"), Title = $"資料{number}", OriginalFileName = $"{number}.pdf", StoredFileName = $"{number:x32}.pdf", Order = number + 1, FileSize = 5, UploadedAt = DateTimeOffset.Now };
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class Environment(string path):IWebHostEnvironment { public string ApplicationName{get;set;}="Tests";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}=path;public string EnvironmentName{get;set;}="Development";public string ContentRootPath{get;set;}=path;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider(); }
}
