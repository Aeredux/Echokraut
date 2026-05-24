using Echokraut.DataClasses;
using Echokraut.Enums;
using Echotools.Logging.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Echokraut.Services.Queue;

/// <summary>
/// Thread-safe implementation of voice message queue using concurrent collections
/// </summary>
public class VoiceMessageQueue : IVoiceMessageQueue
{
    // Priority queues - dialogue gets priority over bubbles
    private readonly ConcurrentQueue<VoiceMessageEntry> _priorityPendingQueue = new();
    private readonly ConcurrentQueue<VoiceMessageEntry> _normalPendingQueue = new();
    private readonly ConcurrentQueue<VoiceMessageEntry> _readyToPlayQueue = new();
    
    // State tracking
    private readonly ConcurrentDictionary<Guid, VoiceMessageEntry> _allEntries = new();
    private readonly ConcurrentDictionary<Guid, VoiceMessageEntry> _generatingEntries = new();
    private VoiceMessageEntry? _currentlyPlaying;
    private readonly object _playingLock = new();
    private volatile bool _selectionMenuActive = false;
    
    // Statistics
    private int _totalCompleted;
    private int _totalCancelled;
    private int _totalFailed;

    public void Enqueue(VoiceMessage message, bool isPriority = false)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        
        var entry = new VoiceMessageEntry(message, isPriority);
        _allEntries[entry.Id] = entry;
        
        if (isPriority)
            _priorityPendingQueue.Enqueue(entry);
        else
            _normalPendingQueue.Enqueue(entry);
    }

    public bool TryDequeuePendingGeneration(out VoiceMessageEntry? entry)
    {
        var pending = _allEntries.Values
            .Where(e => e.State == VoiceMessageState.PendingGeneration)
            .ToList();
        if (pending.Count == 0)
        {
            entry = null;
            return false;
        }

        var pool = pending.Any(e => e.IsPriority)
            ? pending.Where(e => e.IsPriority).ToList()
            : pending;

        entry = SelectDeterministicEntry(pool, null);
        return entry != null;
    }

    public bool TryDequeueReadyToPlay(out VoiceMessageEntry? entry)
    {
        var ready = _allEntries.Values
            .Where(e => e.State == VoiceMessageState.ReadyToPlay)
            .ToList();
        if (ready.Count == 0)
        {
            entry = null;
            return false;
        }

        var pool = ready.Any(e => e.IsPriority)
            ? ready.Where(e => e.IsPriority).ToList()
            : ready;

        entry = SelectDeterministicEntry(pool, _allEntries.Values.ToList());
        return entry != null;
    }

    private VoiceMessageEntry? SelectDeterministicEntry(IReadOnlyList<VoiceMessageEntry> entries, IReadOnlyList<VoiceMessageEntry>? allEntries = null)
    {
        var dialogue = entries.Where(e => IsDialogueSource(e.Message.Source)).ToList();
        if (dialogue.Count > 0)
        {
            // If selection menu is active, block AddonTalk/BattleTalk until player chooses
            if (_selectionMenuActive)
            {
                var nonNpcDialogue = dialogue
                    .Where(e => e.Message.Source != TextSource.AddonTalk && e.Message.Source != TextSource.AddonBattleTalk)
                    .ToList();
                
                if (nonNpcDialogue.Count > 0)
                    dialogue = nonNpcDialogue;
                else
                    return null; // Block everything until choice is made
            }

            return dialogue
                .OrderBy(e => e.Message.EventId?.Id ?? int.MaxValue)
                .ThenBy(e => e.QueuedAt)
                .FirstOrDefault();
        }

        return entries.OrderBy(e => e.QueuedAt).FirstOrDefault();
    }

    private static bool IsDialogueSource(TextSource source)
        => source is TextSource.AddonTalk
            or TextSource.AddonBattleTalk
            or TextSource.AddonSelectString
            or TextSource.AddonCutsceneSelectString
            or TextSource.VoiceTest;

    public void MarkAsGenerating(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            if (entry.State is VoiceMessageState.Cancelled or VoiceMessageState.Completed or VoiceMessageState.Failed)
                return;
            entry.TransitionTo(VoiceMessageState.Generating);
            _generatingEntries[entryId] = entry;
        }
    }

    public void MarkAsReadyToPlay(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            if (entry.State is VoiceMessageState.Cancelled or VoiceMessageState.Completed or VoiceMessageState.Failed)
                return;
            entry.TransitionTo(VoiceMessageState.ReadyToPlay);
            _generatingEntries.TryRemove(entryId, out _);
            _readyToPlayQueue.Enqueue(entry);
        }
    }

    public void MarkAsPlaying(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            if (entry.State is VoiceMessageState.Cancelled or VoiceMessageState.Completed or VoiceMessageState.Failed)
                return;
            entry.TransitionTo(VoiceMessageState.Playing);
            lock (_playingLock)
            {
                _currentlyPlaying = entry;
            }
        }
    }

    public void MarkAsPaused(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            entry.TransitionTo(VoiceMessageState.Paused);
        }
    }

    public void MarkAsCompleted(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            entry.TransitionTo(VoiceMessageState.Completed);
            System.Threading.Interlocked.Increment(ref _totalCompleted);
            lock (_playingLock)
            {
                if (_currentlyPlaying?.Id == entryId)
                    _currentlyPlaying = null;
            }
            _generatingEntries.TryRemove(entryId, out _);
        }
    }

    public void MarkAsCancelled(Guid entryId)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            entry.TransitionTo(VoiceMessageState.Cancelled);
            System.Threading.Interlocked.Increment(ref _totalCancelled);
            lock (_playingLock)
            {
                if (_currentlyPlaying?.Id == entryId)
                    _currentlyPlaying = null;
            }
            _generatingEntries.TryRemove(entryId, out _);
        }
    }

    public void MarkAsFailed(Guid entryId, Exception error)
    {
        if (_allEntries.TryGetValue(entryId, out var entry))
        {
            entry.Error = error;
            entry.TransitionTo(VoiceMessageState.Failed);
            System.Threading.Interlocked.Increment(ref _totalFailed);
            lock (_playingLock)
            {
                if (_currentlyPlaying?.Id == entryId)
                    _currentlyPlaying = null;
            }
            _generatingEntries.TryRemove(entryId, out _);
        }
    }

    public void CancelBySource(TextSource source)
    {
        foreach (var entry in _allEntries.Values.Where(e => e.Message.Source == source))
        {
            if (entry.State != VoiceMessageState.Completed && 
                entry.State != VoiceMessageState.Cancelled &&
                entry.State != VoiceMessageState.Failed)
            {
                MarkAsCancelled(entry.Id);
            }
        }
    }

    public void CancelBySourceOlderThan(TextSource source, int maxExclusiveEventId)
    {
        foreach (var entry in _allEntries.Values.Where(e => e.Message.Source == source && e.Message.EventId != null && e.Message.EventId.Id < maxExclusiveEventId))
        {
            if (entry.State != VoiceMessageState.Completed &&
                entry.State != VoiceMessageState.Cancelled &&
                entry.State != VoiceMessageState.Failed)
            {
                MarkAsCancelled(entry.Id);
            }
        }
    }

    public void CancelAll()
    {
        // Clear all queues
        while (_priorityPendingQueue.TryDequeue(out var entry))
            MarkAsCancelled(entry.Id);
        
        while (_normalPendingQueue.TryDequeue(out var entry))
            MarkAsCancelled(entry.Id);
        
        while (_readyToPlayQueue.TryDequeue(out var entry))
            MarkAsCancelled(entry.Id);
        
        // Cancel generating entries
        foreach (var entry in _generatingEntries.Values)
            MarkAsCancelled(entry.Id);
        
        // Cancel currently playing
        lock (_playingLock)
        {
            if (_currentlyPlaying != null)
            {
                MarkAsCancelled(_currentlyPlaying.Id);
            }
        }
    }

    public void SetSelectionMenuActive(bool active)
    {
        _selectionMenuActive = active;
    }

    public VoiceMessageEntry? GetEntry(Guid entryId)
    {
        _allEntries.TryGetValue(entryId, out var entry);
        return entry;
    }

    public VoiceMessageEntry? GetCurrentlyPlaying()
    {
        lock (_playingLock)
        {
            return _currentlyPlaying;
        }
    }

    public IReadOnlyList<VoiceMessageEntry> GetEntriesByState(VoiceMessageState state)
    {
        return _allEntries.Values
            .Where(e => e.State == state)
            .ToList();
    }

    public QueueStatistics GetStatistics()
    {
        var states = _allEntries.Values
            .GroupBy(e => e.State)
            .ToDictionary(g => g.Key, g => g.Count());
        
        return new QueueStatistics
        {
            PendingGeneration = states.GetValueOrDefault(VoiceMessageState.PendingGeneration, 0),
            Generating = states.GetValueOrDefault(VoiceMessageState.Generating, 0),
            ReadyToPlay = states.GetValueOrDefault(VoiceMessageState.ReadyToPlay, 0),
            Playing = states.GetValueOrDefault(VoiceMessageState.Playing, 0),
            Paused = states.GetValueOrDefault(VoiceMessageState.Paused, 0),
            TotalCompleted = _totalCompleted,
            TotalCancelled = _totalCancelled,
            TotalFailed = _totalFailed
        };
    }

    public void Dispose()
    {
        CancelAll();
        _allEntries.Clear();
        _generatingEntries.Clear();
    }
}
