using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// <see cref="JsonFileStore{T}"/> — ChatHistoryService·ProjectService 가 각자 갖고 있던 저장 로직을
/// 한 곳으로 모은 뒤, 그 동작(원자적 교체·손상 파일 건너뛰기·경로 탈출 차단·조용한 삭제)을 고정한다.
/// </summary>
public class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "jsonstore-" + Guid.NewGuid().ToString("N"));

    private JsonFileStore<ProjectRecord> NewStore() => new(_dir, "TestStore", "테스트");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* 정리 실패는 무시 */ }
        GC.SuppressFinalize(this);
    }

    private static ProjectRecord Record(string id, string name = "p") => new()
    {
        Id = id,
        Name = name,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
        Synced = false,
        RemoteId = null
    };

    [Fact]
    public async Task Save_Then_Load_RoundTrips()
    {
        var store = NewStore();
        await store.SaveAsync("a1", Record("a1", "이름"), CancellationToken.None);

        var loaded = await store.LoadAsync("a1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("a1", loaded!.Id);
        Assert.Equal("이름", loaded.Name);
    }

    [Fact]
    public async Task Save_Overwrites_And_LeavesNoTempFile()
    {
        var store = NewStore();
        await store.SaveAsync("a1", Record("a1", "처음"), CancellationToken.None);
        await store.SaveAsync("a1", Record("a1", "나중"), CancellationToken.None);

        var loaded = await store.LoadAsync("a1", CancellationToken.None);
        Assert.Equal("나중", loaded!.Name);

        // 원자적 교체 — tmp 파일이 남지 않아야 한다.
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(_dir, "*.json"));
    }

    [Fact]
    public async Task Load_ReturnsNull_ForMissing_Blank_And_Corrupt()
    {
        var store = NewStore();
        await store.SaveAsync("seed", Record("seed"), CancellationToken.None);   // 폴더 생성용

        Assert.Null(await store.LoadAsync("nope", CancellationToken.None));
        Assert.Null(await store.LoadAsync("", CancellationToken.None));
        Assert.Null(await store.LoadAsync("   ", CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(_dir, "broken.json"), "{ this is not json");
        Assert.Null(await store.LoadAsync("broken", CancellationToken.None));   // 예외 없이 null
    }

    [Fact]
    public async Task List_SkipsCorruptFiles_AndProjectsEachRecord()
    {
        var store = NewStore();
        await store.SaveAsync("a", Record("a"), CancellationToken.None);
        await store.SaveAsync("b", Record("b"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_dir, "broken.json"), "{ nope");

        var ids = await store.ListAsync(r => r.Id, CancellationToken.None);

        // 손상 파일 하나가 목록 전체를 실패시키지 않는다.
        Assert.Equal(["a", "b"], ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task List_ReturnsEmpty_WhenDirectoryMissing()
    {
        var store = NewStore();   // 아직 아무것도 저장하지 않아 폴더가 없다.
        Assert.Empty(await store.ListAsync(r => r.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesRecord_AndIsSilentWhenMissing()
    {
        var store = NewStore();
        await store.SaveAsync("a", Record("a"), CancellationToken.None);

        await store.DeleteAsync("a", CancellationToken.None);
        Assert.Null(await store.LoadAsync("a", CancellationToken.None));

        // 없는 id·빈 id 삭제는 예외 없이 no-op.
        await store.DeleteAsync("a", CancellationToken.None);
        await store.DeleteAsync("", CancellationToken.None);
    }

    [Fact]
    public async Task PathEscape_IsBlocked_IdIsReducedToFileName()
    {
        var store = NewStore();
        await store.SaveAsync(@"..\..\escape", Record("x"), CancellationToken.None);

        // 파일명만 사용하므로 폴더 밖으로 나가지 않는다.
        var files = Directory.EnumerateFiles(_dir, "*.json").Select(Path.GetFileName).ToList();
        Assert.Equal(["escape.json"], files);
    }

    [Fact]
    public async Task Save_Throws_AgentException_WithLabel_WhenPathUnusable()
    {
        // 저장 폴더 자리에 같은 이름의 '파일'을 만들어 CreateDirectory 를 실패시킨다.
        var blocked = Path.Combine(Path.GetTempPath(), "jsonstore-blocked-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocked, "occupied");
        try
        {
            var store = new JsonFileStore<ProjectRecord>(blocked, "TestStore", "테스트");

            var ex = await Assert.ThrowsAsync<AgentException>(
                () => store.SaveAsync("a", Record("a"), CancellationToken.None));

            Assert.Contains("테스트 저장 실패", ex.Message);   // label 이 문구에 그대로 실린다
            Assert.Contains("a", ex.Message);
        }
        finally
        {
            try { File.Delete(blocked); } catch { /* 정리 실패는 무시 */ }
        }
    }
}
