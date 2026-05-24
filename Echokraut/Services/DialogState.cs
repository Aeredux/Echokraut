using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Echokraut.DataClasses;

namespace Echokraut.Services;

public static class DialogState
{
    private static long _captureSequence;

    public static VoiceMessage? CurrentVoiceMessage;
    public static bool IsVoiced;
    public static Func<Vector2, bool>? IsInsideOwnedWindow;

    public static long NextCaptureSequence() => Interlocked.Increment(ref _captureSequence);

    /// <summary>
    /// Character IDs that have already been resolved via the DB speaker-alias lookup
    /// during the current dialog session. Used by <c>VoiceMessageProcessor</c>'s alias
    /// resolver to prefer NPCs that haven't spoken yet when a fakename like <c>???</c>
    /// matches multiple physically-present characters in the same cutscene. Cleared by
    /// <c>AddonTalkHelper.OnPostUpdate</c> when the AddonTalk window closes.
    /// </summary>
    public static readonly HashSet<int> SpeakersResolvedThisDialog = new();
}
