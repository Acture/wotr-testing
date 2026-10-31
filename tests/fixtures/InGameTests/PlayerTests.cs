using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WotR.Testing.InGame;
using Xunit;

namespace InGameTests;

[CollectionDefinition("In-game")]
public sealed class GameCollection : ICollectionFixture<InGameFixture> { }

[Trait("WotrMode", "ingame")]
[Collection("In-game")]
public sealed class PlayerTests
{
    private readonly InGameFixture game;
    public PlayerTests(InGameFixture game) => this.game = game;

    [Fact]
    public Task Native_object_survives_a_frame_on_the_main_thread() => game.OnMainThread(async context => {
        var marker = context.Own(new GameObject("WotR.Testing.Marker"));
        marker.transform.position = new Vector3(1, 2, 3);
        var frame = Time.frameCount;
        await context.NextFrame();
        Assert.True(Time.frameCount > frame, "NextFrame resumed in the same frame.");
        Assert.Equal(context.MainThreadId, Thread.CurrentThread.ManagedThreadId);
        Assert.Equal(new Vector3(1, 2, 3), marker.transform.position);
        Assert.True(Application.isPlaying);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task Theory_rows_run_on_the_main_thread(int frames) => game.OnMainThread(async context => {
        var start = Time.frameCount;
        for (int index = 0; index < frames; index++) await context.NextFrame();
        Assert.True(Time.frameCount >= start + frames);
        context.AssertMainThread();
    });

    [Fact]
    public async Task Owned_objects_are_destroyed_after_success_and_failure()
    {
        GameObject kept = null, failed = null;
        await game.OnMainThread(context => { kept = context.Own(new GameObject("WotR.Testing.Kept")); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => game.OnMainThread(context => {
            failed = context.Own(new GameObject("WotR.Testing.Failed"));
            throw new InvalidOperationException("deliberate");
        }));
        await game.OnMainThread(context => {
            Assert.True(kept == null, "The owned object of a passing body still exists.");
            Assert.True(failed == null, "The owned object of a failing body still exists.");
            return Task.CompletedTask;
        });
    }
}
