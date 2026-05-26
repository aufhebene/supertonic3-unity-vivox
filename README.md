# Supertonic-3 × Vivox TTS Bridge

Unity project that bridges **[Supertonic-3](https://supertonic.supertone.ai/)** — an offline neural TTS running on `Unity.InferenceEngine` (Sentis) — into a **Vivox** voice channel. Typed text is synthesized locally and injected as the player's voice so other channel participants hear it.

Forked from a Piper + eSpeak-NG implementation; the TTS backend was swapped to Supertonic-3 while preserving the Vivox integration seam.

## Key Features

* **Offline neural TTS**: Supertonic-3 runs entirely on-device via Sentis (flow-matching diffusion vocoder, FP16 models, ~200 MB total in StreamingAssets). No network calls for synthesis.
* **Multilingual**: 31 languages selectable at runtime via the `SupertonicLanguage` enum.
* **Voice style swap**: Drop a `<Name>.json` from the [Supertonic Voice Builder](https://supertonic.supertone.ai/voice-builder) into `Assets/StreamingAssets/Supertonic/VoiceStyles/` and it appears in the UI dropdown automatically.
* **Vivox injection**: Synthesized PCM is written to a WAV and fed into Vivox via `StartAudioInjection`, so remote participants hear it as if it were your mic.
* **No native plugins**: Unlike the Piper-era code, no `libespeak-ng` or platform-specific binaries are required.

## Architecture

```
text → SupertonicTtsManager.Synthesize()
         ├── duration_predictor → text_encoder → vector_estimator (× totalStep) → vocoder
         └── OnAudioDataGenerated(float[] pcm, int sampleRate)
                                  │
                                  ▼
                       VivoxVoiceManager
                         └── write WAV → VivoxService.StartAudioInjection(path)
```

Two singletons wire it together:

* **`SupertonicTtsManager`** (`Assets/Scripts/Supertonic/`) — owns four Sentis `Worker`s, lazy-loads FP16 `.sentis` models and config from `StreamingAssets/Supertonic/`, chunks input (120 chars for ko/ja, 300 otherwise), runs the inference chain, and broadcasts PCM via `OnAudioDataGenerated`.
* **`VivoxVoiceManager`** (`Assets/Scripts/ChatChannelSample/Managers/`) — subscribes to that event, writes the PCM as a WAV under `Application.persistentDataPath`, and calls `VivoxService.Instance.StartAudioInjection(...)`.

## Requirements

* **Unity Editor**: `6000.4.5f1` or newer
* **Render pipeline**: URP 17.x
* **Sentis**: `com.unity.ai.inference 2.6.1`
* **Vivox**: `com.unity.services.vivox 16.6.2` + a Vivox developer account

## Getting Started

1. Clone the repo and open it in Unity 6000.4.5f1+. `.sln` / `.csproj` regenerate on open.
2. Open `Assets/Scenes/VivoxSample/MainScene.unity` (the only scene in Build Settings).
3. Configure Vivox credentials on the `VivoxVoiceManager` component — fill `_key`, `_issuer`, `_domain`, `_server` from your Vivox developer portal, *or* rely on Unity Authentication (auto-enabled when the auth package is present).
4. Enter Play mode. Log in, join the lobby, and use the text chat — typed messages are synthesized locally and injected into the Vivox channel.

## Adding Voices

Drop a Supertonic voice JSON into `Assets/StreamingAssets/Supertonic/VoiceStyles/<Name>.json` — `VoiceStyleSelector` picks it up at runtime. Generate custom voices with the [Supertonic Voice Builder](https://supertonic.supertone.ai/voice-builder).

## Notes

* The four `.sentis` models in `StreamingAssets/Supertonic/Models/` total ~200 MB. For mobile builds, consider Addressables before shipping.
* `totalStep` (default 8) on the diffusion loop controls latency. Lower to 6 if first-syllable latency feels too long.
* Vivox audio injection is file-based, not streaming — rapid synthesis calls are dropped while `busy`.

## Credits

TTS powered by Supertone Inc.'s [Supertonic-3](https://github.com/supertone-inc/supertonic) — a lightweight, on-device, multilingual neural TTS.
