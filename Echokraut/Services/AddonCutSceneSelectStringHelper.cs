using Dalamud.Plugin.Services;
using Echokraut.DataClasses;
using FFXIVClientStructs.FFXIV.Client.UI;
using Echokraut.Enums;
using Echotools.Logging.Enums;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Linq;
using System.Collections.Generic;
using Echokraut.Helper.Functional;
using Echokraut.Services;
using Echotools.Logging.Services;
using System;

namespace Echokraut.Services;

public class AddonCutSceneSelectStringHelper : IAddonCutSceneSelectStringHelper
{
    private record struct AddonCutSceneSelectStringState(string? Speaker, string? Text, AddonPollSource PollSource);

    // Injected dependencies
    private readonly IVoiceMessageProcessor _voiceProcessor;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly ILogService _log;
    private readonly Configuration _configuration;
    private readonly IAddonCancelService _cancelService;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly IGameObjectService _gameObjects;
    private readonly ITextProcessingService _textProcessing;

    private List<string> options = new List<string>();

    public AddonCutSceneSelectStringHelper(
        IVoiceMessageProcessor voiceProcessor,
        IAddonLifecycle addonLifecycle,
        ILogService log,
        Configuration configuration,
        IAddonCancelService cancelService,
        IAudioPlaybackService audioPlayback,
        IGameObjectService gameObjects,
        ITextProcessingService textProcessing)
    {
        _voiceProcessor = voiceProcessor ?? throw new ArgumentNullException(nameof(voiceProcessor));
        _addonLifecycle = addonLifecycle ?? throw new ArgumentNullException(nameof(addonLifecycle));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _cancelService = cancelService ?? throw new ArgumentNullException(nameof(cancelService));
        _audioPlayback = audioPlayback ?? throw new ArgumentNullException(nameof(audioPlayback));
        _gameObjects = gameObjects ?? throw new ArgumentNullException(nameof(gameObjects));
        _textProcessing = textProcessing ?? throw new ArgumentNullException(nameof(textProcessing));
        HookIntoAddonLifecycle();
    }

    private void HookIntoAddonLifecycle()
    {
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CutSceneSelectString", OnPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CutSceneSelectString", OnPreFinalize);
    }

    private unsafe void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!_configuration.Enabled) return;
        if (!_configuration.VoicePlayerChoicesCutscene) return;

        var list = ((AddonCutSceneSelectString*)args.Addon.Address)->OptionList;
        GetAddonStrings(list);

        if (list is not null)
        {
            var selectedItem = list->SelectedItemIndex;
            var selectedPreview = selectedItem >= 0 && selectedItem < options.Count ? options[selectedItem] : "";
            var seq = DialogState.NextCaptureSequence();
            var eventId = new EKEventId(0, TextSource.AddonCutsceneSelectString);
            _log.Info(nameof(OnPostSetup),
                $"CAPTURE seq={seq} src=AddonCutsceneSelectStringMenuVisible selectedIndex={selectedItem} selectedPreview='{selectedPreview}' options={options.Count}",
                eventId);
        }
    }

    private unsafe void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        if (!_configuration.Enabled) return;
        if (!_configuration.VoicePlayerChoicesCutscene) return;

        HandleSelectedString(((AddonCutSceneSelectString*)args.Addon.Address)->OptionList);
    }

    private unsafe void GetAddonStrings(AtkComponentList* list)
    {
        if (list is null) return;

        options.Clear();

        foreach (var index in Enumerable.Range(0, list->ListLength))
        {
            var listItemRenderer = list->ItemRendererList[index].AtkComponentListItemRenderer;
            if (listItemRenderer is null) continue;

            var buttonTextNode = listItemRenderer->AtkComponentButton.ButtonTextNode;
            if (buttonTextNode is null) continue;

            var buttonText = TalkTextHelper.ReadStringNode(buttonTextNode->NodeText);

            options.Add(buttonText);
        }
    }

    private unsafe void HandleSelectedString(AtkComponentList* list)
    {
        if (list is null) return;

        var selectedItem = list->SelectedItemIndex;
        if (selectedItem < 0 || selectedItem >= options.Count) return;

        var selectedString = options[selectedItem];
        var localPlayerName = _gameObjects.LocalPlayer?.Name;

        HandleChange(new AddonCutSceneSelectStringState()
        {
            PollSource = AddonPollSource.FrameworkUpdate,
            Text = selectedString,
            Speaker = localPlayerName?.TextValue ?? "PLAYER"
        });
    }

    private void HandleChange(AddonCutSceneSelectStringState state)
    {
        var (speaker, text, pollSource) = state;

        if (state == default)
        {
            return;
        }

        var _baseId = _log.Start(nameof(HandleChange), TextSource.AddonCutsceneSelectString);
        var eventId = new EKEventId(_baseId.Id, _baseId.TextSource);
        var captureSeq = DialogState.NextCaptureSequence();

        _log.Info(nameof(HandleChange),
            $"CAPTURE seq={captureSeq} src=AddonCutsceneSelectString speaker='{speaker ?? ""}' raw='{text ?? ""}'",
            eventId);

        var current = DialogState.CurrentVoiceMessage;
        if (current?.Source is TextSource.AddonSelectString or TextSource.AddonCutsceneSelectString)
            _cancelService.Cancel(current);

        text = _textProcessing.NormalizePunctuation(text);

        _log.Debug(nameof(HandleChange), $"\"{text}\"", eventId);


        // Find the game object this speaker is representing
        var speakerObj = speaker != null ? _gameObjects.GetGameObjectByName(speaker, eventId) : null;

        if (speakerObj != null)
        {
            _log.Debug(nameof(HandleChange), $"speakerobject: ({speakerObj.Name})", eventId);
            _ = _voiceProcessor.ProcessSpeechAsync(eventId, speakerObj, speakerObj.Name, text);
        }
        else
        {
            _log.Debug(nameof(HandleChange), $"object: ({state.Speaker})", eventId);
            _ = _voiceProcessor.ProcessSpeechAsync(eventId, null, state.Speaker ?? "PLAYER", text);
        }
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CutSceneSelectString", OnPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "CutSceneSelectString", OnPreFinalize);
    }
}
