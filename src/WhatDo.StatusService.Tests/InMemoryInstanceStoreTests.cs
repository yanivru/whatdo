using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InMemoryInstanceStoreTests
{
    [Fact]
    public void Upsert_NewInstance_AppearsInGetAll()
    {
        var store = new InMemoryInstanceStore();

        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("whatdo-a1b2c3d4", all[0].InstanceId);
        Assert.Equal("whatdo", all[0].Name);
        Assert.Equal(HookEventStatus.Working, all[0].Status);
        Assert.Equal("UserPromptSubmit", all[0].LastEvent);
    }

    [Fact]
    public void Upsert_ExistingInstance_UpdatesStatusInPlace()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Idle, "Stop");

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal(HookEventStatus.Idle, all[0].Status);
        Assert.Equal("Stop", all[0].LastEvent);
    }

    [Fact]
    public void Remove_ExistingInstance_RemovesIt()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        store.Remove("whatdo-a1b2c3d4");

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Remove_UnknownInstance_DoesNotThrow()
    {
        var store = new InMemoryInstanceStore();

        var exception = Record.Exception(() => store.Remove("does-not-exist"));

        Assert.Null(exception);
    }

    [Fact]
    public void TryGet_ExistingInstance_ReturnsIt()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");

        var found = store.TryGet("whatdo-a1b2c3d4", out var info);

        Assert.True(found);
        Assert.Equal(HookEventStatus.Working, info!.Status);
    }

    [Fact]
    public void TryGet_UnknownInstance_ReturnsFalse()
    {
        var store = new InMemoryInstanceStore();

        var found = store.TryGet("nope", out var info);

        Assert.False(found);
        Assert.Null(info);
    }

    [Fact]
    public void GetAll_MultipleInstances_ReturnsAll()
    {
        var store = new InMemoryInstanceStore();
        store.Upsert("whatdo-a1b2c3d4", "whatdo", @"C:\repos\whatdo", HookEventStatus.Working, "UserPromptSubmit");
        store.Upsert("other-e5f6a7b8", "other", @"C:\repos\other", HookEventStatus.Idle, "Stop");

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
    }
}
