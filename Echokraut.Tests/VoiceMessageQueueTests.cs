using Dalamud.Game.ClientState.Objects.Enums;
using Echokraut.DataClasses;
using Echotools.Logging.DataClasses;
using Echokraut.Enums;
using Echotools.Logging.Enums;
using Echokraut.Services.Queue;
using Xunit;

namespace Echokraut.Tests;

public class VoiceMessageQueueTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VoiceMessage MakeMessage(TextSource source = TextSource.AddonTalk, string text = "Hello.")
    {
        return new VoiceMessage
        {
            Text = text,
            OriginalText = text,
            Speaker = new NpcMapData(ObjectKind.EventNpc) { Name = "TestNpc" },
            Source = source,
            EventId = new EKEventId(1, source),
        };
    }

    // ── Enqueue / Dequeue ─────────────────────────────────────────────────────

    [Fact]
    public void Enqueue_ThenTryDequeue_ReturnsEntry()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());

        var found = queue.TryDequeuePendingGeneration(out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
    }

    [Fact]
    public void Enqueue_NullMessage_Throws()
    {
        var queue = new VoiceMessageQueue();

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
    }

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var queue = new VoiceMessageQueue();

        var found = queue.TryDequeuePendingGeneration(out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void PriorityMessage_DequeuedBeforeNormal()
    {
        var queue = new VoiceMessageQueue();
        var normal  = MakeMessage(text: "Normal");
        var priority = MakeMessage(text: "Priority");

        queue.Enqueue(normal, isPriority: false);
        queue.Enqueue(priority, isPriority: true);

        queue.TryDequeuePendingGeneration(out var first);

        Assert.Equal("Priority", first!.Message.Text);
    }

    [Fact]
    public void PendingDialogue_DequeuesByEventIdOrder()
    {
        var queue = new VoiceMessageQueue();
        var later = MakeMessage(TextSource.AddonTalk, "Later");
        later.EventId = new EKEventId(200, TextSource.AddonTalk);
        var earlier = MakeMessage(TextSource.AddonTalk, "Earlier");
        earlier.EventId = new EKEventId(100, TextSource.AddonTalk);

        queue.Enqueue(later, isPriority: true);
        queue.Enqueue(earlier, isPriority: true);

        var found = queue.TryDequeuePendingGeneration(out var first);
        Assert.True(found);
        Assert.Equal("Earlier", first!.Message.Text);
    }

    // ── State transitions ─────────────────────────────────────────────────────

    [Fact]
    public void MarkAsGenerating_SetsState()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);

        queue.MarkAsGenerating(entry!.Id);

        Assert.Equal(VoiceMessageState.Generating, queue.GetEntry(entry.Id)!.State);
    }

    [Fact]
    public void MarkAsReadyToPlay_EntryAppearsInReadyQueue()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);

        queue.MarkAsReadyToPlay(entry.Id);

        var found = queue.TryDequeueReadyToPlay(out var ready);
        Assert.True(found);
        Assert.Equal(entry.Id, ready!.Id);
    }

    [Fact]
    public void ReadyDialogue_DequeuesByEventIdOrder()
    {
        var queue = new VoiceMessageQueue();

        var later = MakeMessage(TextSource.AddonTalk, "LaterReady");
        later.EventId = new EKEventId(40, TextSource.AddonTalk);
        var earlier = MakeMessage(TextSource.AddonTalk, "EarlierReady");
        earlier.EventId = new EKEventId(30, TextSource.AddonTalk);

        queue.Enqueue(later, isPriority: true);
        queue.Enqueue(earlier, isPriority: true);

        queue.TryDequeuePendingGeneration(out var a);
        queue.MarkAsGenerating(a!.Id);
        queue.MarkAsReadyToPlay(a.Id);

        queue.TryDequeuePendingGeneration(out var b);
        queue.MarkAsGenerating(b!.Id);
        queue.MarkAsReadyToPlay(b.Id);

        var found = queue.TryDequeueReadyToPlay(out var firstReady);
        Assert.True(found);
        Assert.Equal("EarlierReady", firstReady!.Message.Text);
    }

    [Fact]
    public void MarkAsPlaying_SetsCurrentlyPlaying()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsReadyToPlay(entry.Id);
        queue.TryDequeueReadyToPlay(out _);

        queue.MarkAsPlaying(entry.Id);

        Assert.Equal(entry.Id, queue.GetCurrentlyPlaying()!.Id);
    }

    [Fact]
    public void MarkAsCompleted_ClearsCurrentlyPlaying()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsReadyToPlay(entry.Id);
        queue.TryDequeueReadyToPlay(out _);
        queue.MarkAsPlaying(entry.Id);

        queue.MarkAsCompleted(entry.Id);

        Assert.Null(queue.GetCurrentlyPlaying());
        Assert.Equal(VoiceMessageState.Completed, queue.GetEntry(entry.Id)!.State);
    }

    [Fact]
    public void MarkAsFailed_SetsErrorAndState()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);

        var error = new Exception("backend down");
        queue.MarkAsFailed(entry!.Id, error);

        var result = queue.GetEntry(entry.Id)!;
        Assert.Equal(VoiceMessageState.Failed, result.State);
        Assert.Same(error, result.Error);
    }

    // ── CancelAll ─────────────────────────────────────────────────────────────

    [Fact]
    public void CancelAll_CancelsEverything()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(text: "A"));
        queue.Enqueue(MakeMessage(text: "B"), isPriority: true);
        queue.TryDequeuePendingGeneration(out var generating);
        queue.MarkAsGenerating(generating!.Id);

        queue.CancelAll();

        var stats = queue.GetStatistics();
        Assert.Equal(0, stats.PendingGeneration);
        Assert.Equal(0, stats.Generating);
        Assert.True(stats.TotalCancelled >= 2);
    }

    [Fact]
    public void CancelAll_ClearsCurrentlyPlaying()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsReadyToPlay(entry.Id);
        queue.TryDequeueReadyToPlay(out _);
        queue.MarkAsPlaying(entry.Id);

        queue.CancelAll();

        Assert.Null(queue.GetCurrentlyPlaying());
    }

    // ── CancelBySource ────────────────────────────────────────────────────────

    [Fact]
    public void CancelBySource_OnlyCancelsMatchingSource()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(TextSource.AddonTalk));
        queue.Enqueue(MakeMessage(TextSource.AddonBubble));

        queue.CancelBySource(TextSource.AddonTalk);

        var talkEntries = queue.GetEntriesByState(VoiceMessageState.Cancelled)
            .Where(e => e.Message.Source == TextSource.AddonTalk).ToList();
        var bubbleEntries = queue.GetEntriesByState(VoiceMessageState.Cancelled)
            .Where(e => e.Message.Source == TextSource.AddonBubble).ToList();

        Assert.Single(talkEntries);
        Assert.Empty(bubbleEntries);
    }

    [Fact]
    public void CancelBySource_AlreadyCompletedNotAffected()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(TextSource.AddonTalk));
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsReadyToPlay(entry.Id);
        queue.TryDequeueReadyToPlay(out _);
        queue.MarkAsPlaying(entry.Id);
        queue.MarkAsCompleted(entry.Id);

        queue.CancelBySource(TextSource.AddonTalk);

        Assert.Equal(VoiceMessageState.Completed, queue.GetEntry(entry.Id)!.State);
    }

    [Fact]
    public void CancelBySourceOlderThan_CancelsOnlyOlderEntries()
    {
        var queue = new VoiceMessageQueue();

        var older = MakeMessage(TextSource.AddonTalk, "Old");
        older.EventId = new EKEventId(10, TextSource.AddonTalk);
        var newer = MakeMessage(TextSource.AddonTalk, "New");
        newer.EventId = new EKEventId(20, TextSource.AddonTalk);

        queue.Enqueue(older);
        queue.Enqueue(newer);

        queue.CancelBySourceOlderThan(TextSource.AddonTalk, 15);

        var cancelled = queue.GetEntriesByState(VoiceMessageState.Cancelled)
            .Where(e => e.Message.Source == TextSource.AddonTalk)
            .Select(e => e.Message.Text)
            .ToList();

        Assert.Contains("Old", cancelled);
        Assert.DoesNotContain("New", cancelled);
    }

    [Fact]
    public void MarkAsReadyToPlay_DoesNotReviveCancelledEntry()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(TextSource.AddonTalk));
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsCancelled(entry.Id);

        queue.MarkAsReadyToPlay(entry.Id);

        var found = queue.TryDequeueReadyToPlay(out _);
        Assert.False(found);
        Assert.Equal(VoiceMessageState.Cancelled, queue.GetEntry(entry.Id)!.State);
    }

    // ── Statistics ────────────────────────────────────────────────────────────

    [Fact]
    public void GetStatistics_ReflectsCurrentState()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(text: "A")); // pending
        queue.Enqueue(MakeMessage(text: "B")); // will become generating

        queue.TryDequeuePendingGeneration(out var b);
        queue.MarkAsGenerating(b!.Id);

        var stats = queue.GetStatistics();

        Assert.Equal(1, stats.PendingGeneration);
        Assert.Equal(1, stats.Generating);
    }

    [Fact]
    public void GetStatistics_TotalCompletedCounts()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage());
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);
        queue.MarkAsReadyToPlay(entry.Id);
        queue.TryDequeueReadyToPlay(out _);
        queue.MarkAsPlaying(entry.Id);
        queue.MarkAsCompleted(entry.Id);

        Assert.Equal(1, queue.GetStatistics().TotalCompleted);
    }

    // ── GetEntriesByState ─────────────────────────────────────────────────────

    [Fact]
    public void GetEntriesByState_ReturnsOnlyMatchingState()
    {
        var queue = new VoiceMessageQueue();
        queue.Enqueue(MakeMessage(text: "A"));
        queue.Enqueue(MakeMessage(text: "B"));
        queue.TryDequeuePendingGeneration(out var entry);
        queue.MarkAsGenerating(entry!.Id);

        var pending    = queue.GetEntriesByState(VoiceMessageState.PendingGeneration);
        var generating = queue.GetEntriesByState(VoiceMessageState.Generating);

        Assert.Single(pending);
        Assert.Single(generating);
    }

    // ── VoiceMessageEntry timestamps ──────────────────────────────────────────

    [Fact]
    public void TransitionToGenerating_SetsStartedGeneratingAt()
    {
        var entry = new VoiceMessageEntry(MakeMessage());
        entry.TransitionTo(VoiceMessageState.Generating);

        Assert.NotNull(entry.StartedGeneratingAt);
    }

    [Fact]
    public void TransitionToCompleted_SetsCompletedAt()
    {
        var entry = new VoiceMessageEntry(MakeMessage());
        entry.TransitionTo(VoiceMessageState.Completed);

        Assert.NotNull(entry.CompletedAt);
        Assert.NotNull(entry.GetTotalProcessingTime());
    }
}
