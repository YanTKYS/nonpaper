using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using NonPaper.Models;
using NonPaper.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NonPaper.Tests;

public sealed class EventRepositoryTests : IDisposable
{
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

    private static EventService Service(IEventRepository repository) => new(
        repository,
        new ConfigurationBuilder().Build(),
        NullLogger<EventService>.Instance);

    private static EventRecord Event()=>new(){Id=EventService.NewId(),Title="庁内会議",StartsAt=DateTimeOffset.Now,EndsAt=DateTimeOffset.Now.AddHours(1),CreatedAt=DateTimeOffset.Now,UpdatedAt=DateTimeOffset.Now,ManagementTokenHash=EventService.HashToken("test")};
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class Environment(string path):IWebHostEnvironment { public string ApplicationName{get;set;}="Tests";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}=path;public string EnvironmentName{get;set;}="Development";public string ContentRootPath{get;set;}=path;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider(); }
}
