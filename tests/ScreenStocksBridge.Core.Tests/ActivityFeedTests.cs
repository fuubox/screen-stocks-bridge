using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace ScreenStocksBridge;

public sealed class ActivityFeedTests
{
    [Fact]
    public void PinRoutesNavigationWithoutSwitchingAndReleaseFollowsLatestGraph()
    {
        double now = 0;
        var native = new List<string>();
        var feed = new ActivityFeed(() => now, native.Add);
        Assert.Equal("A", feed.RouteGraphFocus("A"));
        feed.Pin("owner", "B", false);
        Assert.Equal("B", feed.RouteGraphFocus("C"));
        Assert.Equal(new[] { "B" }, native);
        feed.Unpin("owner"); // no five-second wait to release
        Assert.Equal(new[] { "B", "C" }, native);
        Assert.Null(feed.Owner);
        Assert.Equal("follow_graph", feed.Mode);
    }

    [Fact]
    public void TakeoverRequiresExplicitAdmissionAndOldDisconnectCannotReleaseReplacement()
    {
        double now = 0;
        var native = new List<string>();
        var feed = new ActivityFeed(() => now, native.Add);
        feed.RouteGraphFocus("graph");
        feed.Pin("first", "A", false);
        Assert.Equal("ownership_conflict", Assert.Throws<ActivityError>(() => feed.Pin("second", "B", false)).Code);
        Assert.Equal("rate_limited", Assert.Throws<ActivityError>(() => feed.Pin("second", "B", true)).Code);
        Assert.Equal("first", feed.Owner);
        Assert.Equal(new[] { "A" }, native);
        now = 5;
        feed.Pin("second", "B", true);
        feed.Disconnect("first");
        Assert.Equal("second", feed.Owner);
        Assert.Equal("B", feed.EffectiveStock);
        Assert.Equal("ownership_conflict", Assert.Throws<ActivityError>(() => feed.Unpin("first")).Code);
        feed.Disconnect("second");
        Assert.Null(feed.Owner);
        Assert.Equal("graph", native.Last());
    }

    [Fact]
    public void NativeFailureDoesNotTransferOwnershipOrConsumeCooldown()
    {
        double now = 0;
        var fail = false;
        var feed = new ActivityFeed(() => now, _ => { if (fail) throw new InvalidOperationException(); });
        feed.Pin("first", "A", false);
        now = 5; fail = true;
        Assert.Throws<InvalidOperationException>(() => feed.Pin("second", "B", true));
        Assert.Equal("first", feed.Owner);
        Assert.Equal("A", feed.EffectiveStock);
        fail = false;
        feed.Pin("second", "B", true);
        Assert.Equal("second", feed.Owner);
    }

    [Fact]
    public void SnapshotPagesStayAtOneRevisionAndExpireExplicitly()
    {
        double now = 0;
        var feed = new ActivityFeed(() => now, _ => { });
        var rows = Enumerable.Range(0, 70).ToDictionary(i => "sample:" + i, i => "{\"sampleTick\":" + i + "}");
        feed.Publish("{\"stockId\":\"A\"}", rows);
        var first = JsonNode.Parse(feed.Snapshot("reader", "", 0))!;
        var id = first["snapshotId"]!.GetValue<string>();
        rows.Clear(); feed.Publish("{\"stockId\":\"B\"}", rows);
        var next = JsonNode.Parse(feed.Snapshot("reader", id, 32))!;
        Assert.Equal(1, next["revision"]!.GetValue<int>());
        Assert.Equal("A", next["state"]!["stockId"]!.GetValue<string>());
        Assert.Equal(32, next["rows"]!.AsArray().Count);
        Assert.Equal("snapshot_expired", Assert.Throws<ActivityError>(() => feed.Snapshot("other", id, 32)).Code);
        now = 31;
        Assert.Equal("snapshot_expired", Assert.Throws<ActivityError>(() => feed.Snapshot("reader", id, 32)).Code);
    }

    [Fact]
    public void RevisionsOutsideLatestPageAndRemovalsArePublishedWithOrderedChunks()
    {
        var feed = new ActivityFeed(() => 0, _ => { });
        var events = new List<IReadOnlyList<string>>();
        feed.Changed += events.Add;
        var rows = Enumerable.Range(0, 100).ToDictionary(i => "sample:" + i, i => "{\"up\":0}");
        feed.Publish("{}", rows);
        Assert.Equal(4, events[0].Count);
        rows["sample:0"] = "{\"up\":9}";
        rows.Remove("sample:1");
        feed.Publish("{}", rows);
        var change = JsonNode.Parse(events[1][0])!;
        Assert.Equal(1, change["previousRevision"]!.GetValue<int>());
        Assert.Equal(2, change["revision"]!.GetValue<int>());
        Assert.Contains(change["changes"]!.AsArray(), x => x!["op"]!.GetValue<string>() == "remove" && x["key"]!.GetValue<string>() == "sample:1");
        Assert.Contains(change["changes"]!.AsArray(), x => x!["op"]!.GetValue<string>() == "upsert" && x["value"]!["up"]!.GetValue<int>() == 9);
        feed.Publish("{}", rows);
        Assert.Equal(2, events.Count); // unchanged reads are not new flow
        var generation = feed.Generation;
        feed.Reset(); feed.Publish("{}", rows);
        Assert.NotEqual(generation, feed.Generation);
        Assert.Equal(0, JsonNode.Parse(events.Last()[0])!["previousRevision"]!.GetValue<int>());
    }

    [Fact]
    public void DisconnectInvalidatesOnlyItsSnapshotsAndEndsAuthorityDespiteNativeFailure()
    {
        var fail = false;
        var feed = new ActivityFeed(() => 0, _ => { if (fail) throw new InvalidOperationException(); });
        feed.Pin("owner", "A", false);
        var id = JsonNode.Parse(feed.Snapshot("owner", "", 0))!["snapshotId"]!.GetValue<string>();
        fail = true;
        Assert.Throws<InvalidOperationException>(() => feed.Disconnect("owner"));
        Assert.Null(feed.Owner);
        Assert.Equal("snapshot_expired", Assert.Throws<ActivityError>(() => feed.Snapshot("owner", id, 0)).Code);
    }
}
