using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using NonPaper.Models;
using NonPaper.Services;
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

    [Fact] public async Task ConcurrentUpdates_DoNotLoseChanges()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => repository.UpdateAsync(value.Id, current =>
        {
            current.Documents.Add(Document(i));
            return true;
        })));

        var loaded = await repository.GetAsync(value.Id);
        Assert.Equal(20, loaded!.Documents.Count);
        Assert.Equal(20, loaded.Documents.Select(x => x.Id).Distinct().Count());
    }

    [Fact] public async Task UpdateAfterDelete_DoesNotRecreateEvent()
    {
        var repository = Repository;
        var value = Event();
        await repository.SaveAsync(value);
        await repository.DeleteAsync(value.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.UpdateAsync(value.Id, current => { current.Title = "復活"; return true; }));
        Assert.False(Directory.Exists(Path.Combine(root, value.Id)));
    }

    [Theory]
    [InlineData("{ invalid")]
    [InlineData("{\"id\":\"00000000000000000000000000000000\",\"title\":\"x\",\"status\":\"draft\",\"managementTokenHash\":\"bad\"}")]
    public async Task CorruptEventData_HasDedicatedException(string json)
    {
        var repository = Repository;
        var id = new string('0', 32);
        Directory.CreateDirectory(Path.Combine(root, id));
        await File.WriteAllTextAsync(Path.Combine(root, id, "event.json"), json);

        var exception = await Assert.ThrowsAsync<DataCorruptionException>(() => repository.GetAsync(id));
        Assert.Equal(id, exception.EventId);
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

    private static EventRecord Event()=>new(){Id=EventService.NewId(),Title="庁内会議",StartsAt=DateTimeOffset.Now,EndsAt=DateTimeOffset.Now.AddHours(1),CreatedAt=DateTimeOffset.Now,UpdatedAt=DateTimeOffset.Now,ManagementTokenHash=EventService.HashToken("test")};
    private static DocumentRecord Document(int number) => new() { Id = number.ToString("x32"), Title = $"資料{number}", OriginalFileName = $"{number}.pdf", StoredFileName = $"{number:x32}.pdf", Order = number + 1, FileSize = 5, UploadedAt = DateTimeOffset.Now };
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class Environment(string path):IWebHostEnvironment { public string ApplicationName{get;set;}="Tests";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}=path;public string EnvironmentName{get;set;}="Development";public string ContentRootPath{get;set;}=path;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider(); }
}
