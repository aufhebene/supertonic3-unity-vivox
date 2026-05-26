# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity project that bridges **Supertonic-3** (offline neural TTS via flow-matching diffusion, running through `Unity.InferenceEngine` / Sentis) into a **Vivox** voice channel. Typed text is synthesized locally and injected as the player's voice into Vivox so other channel participants hear it.

Originally forked from a Piper + eSpeak-NG implementation; the TTS backend was swapped to Supertonic-3 while preserving the Vivox integration seam.

- Unity Editor: **6000.4.5f1** (see `ProjectSettings/ProjectVersion.txt`)
- Render pipeline: URP 17.x
- Sentis: `com.unity.ai.inference 2.6.1`
- `.sln` / `.csproj` files are gitignored — Unity regenerates them when the project is opened.
- The root `README.md` still references the Piper-era pipeline; trust this file and the code, not the README.
- Longer-form design notes from the Piper era live in `docs/`. They are out of date with respect to TTS — only the Vivox channel/auth parts remain accurate.

## Build / run

No CLI build script. Open the project in Unity 6000.4.5f1+ and:

- Main scene: `Assets/Scenes/VivoxSample/MainScene.unity` (only entry in Build Settings).
- Other scenes: `EchoTest.unity`, `AudioTapsTest.unity` (Vivox echo + taps demos; unrelated to TTS).
- Vivox credentials: serialized fields on the `VivoxVoiceManager` component (`_key`, `_issuer`, `_domain`, `_server`) — or via Unity Authentication when the package is present (the `AUTH_PACKAGE_PRESENT` define is set automatically by `ChatChannelSample.asmdef`'s `versionDefines`).
- Tests run via Unity Test Framework inside the Editor's Test Runner window.

## Architecture

The TTS-to-Vivox pipeline is a chain of two singletons. Understanding their wiring is the main thing to know:

1. **`SupertonicTtsManager`** (`Assets/Scripts/Supertonic/SupertonicTtsManager.cs`) — singleton MonoBehaviour holding four Sentis `Worker`s (duration_predictor, text_encoder, vector_estimator, vocoder).
   - `Awake()` enforces singleton + optional `localAudioSource`. `Start()` runs `Warmup()` (a 1-step `"."` synthesis) to amortize first-call latency.
   - `EnsureLoaded()` lazy-loads four FP16 `.sentis` models from `StreamingAssets/Supertonic/Models/`, plus `tts.json` (sample rate, chunk sizes) and `unicode_indexer.json` (codepoint → token id) from `StreamingAssets/Supertonic/Config/`.
   - `Synthesize(text)` chunks the text (**120 chars for ko/ja, 300 chars otherwise**), runs the inference chain per chunk (duration → text encoder → flow-matching diffusion loop × `totalStep` → vocoder), and produces `float[]` mono PCM at the configured sample rate (22050 Hz by default).
   - **Output is broadcast via the `OnAudioDataGenerated(float[] audioData, int sampleRate)` event** — this is the integration seam preserved from the Piper-era code.
   - `LoadVoiceStyle(name)` and `SetLanguage(SupertonicLanguage)` swap voice/language at runtime without reloading models.
   - `playLocally` (default `false`) gates local `AudioSource` playback. Leave off when Vivox is consuming the output to avoid double playback.

2. **`VivoxVoiceManager`** (`Assets/Scripts/ChatChannelSample/Managers/VivoxVoiceManager.cs`) — singleton wrapping `Unity.Services.Vivox`.
   - In `Awake()`, subscribes to `SupertonicTtsManager.Instance.OnAudioDataGenerated`.
   - When `useSupertonic == true`, the handler writes the float buffer to `Application.persistentDataPath/SupertonicAudio.wav` and calls `VivoxService.Instance.StartAudioInjection(filePath)`. (`StartAudioInjection` is file-based, hence the WAV detour.)
   - `TextToSpeechSendMessage(message)` routes to `SupertonicTtsManager.Synthesize` when `useSupertonic`, otherwise to Vivox's built-in `VivoxService.TextToSpeechSendMessage`.

3. **`AudioTapsManager`** — separate from the TTS path; manages Vivox audio taps (echo / "evil" mixer effects).

4. **UI layer** (`Assets/Scripts/ChatChannelSample/UI/` and root of `ChatChannelSample/`):
   - `VoiceStyleSelector.cs` — TMP_Dropdown populated from `*.json` files in `StreamingAssets/Supertonic/VoiceStyles/`; selection calls `SupertonicTtsManager.LoadVoiceStyle`.
   - `LanguageSelector.cs` — TMP_Dropdown populated from the `SupertonicLanguage` enum (31 languages); selection calls `SupertonicTtsManager.SetLanguage`.
   - login → lobby → text chat screens reuse the original Piper-era UI; `TextToSpeechSendMessage` is the only hook into TTS.

### Things to watch when editing

- **Three assemblies**:
  - `Assets/Scripts/ChatChannelSample.asmdef` — runtime code under `Assets/Scripts/` (includes `Supertonic/`, `ChatChannelSample/`). References `Unity.InferenceEngine`, `Unity.Services.Vivox`, `Unity.Services.Authentication`, `VivoxUnity`, `Unity.TextMeshPro`.
  - `Assets/ChatChannelSample/Editor/ChatChannelSample.Editor.asmdef` — Editor-only helpers. References `Unity.Services.Vivox.Editor`.
  - `Assets/ChatChannelSample/` (no `Scripts/` prefix) otherwise holds `Prefabs/`, `Sprites/`, `Textures/` — no runtime code.
- **Adding a new Supertonic voice style**: drop `<Name>.json` into `Assets/StreamingAssets/Supertonic/VoiceStyles/`. `VoiceStyleSelector` and `SupertonicTtsManager.GetAvailableVoiceStyles()` pick it up automatically (Android/WebGL fall back to a hardcoded `DefaultVoiceStyles` list since `Directory.GetFiles` doesn't work in those StreamingAssets layouts).
- **Custom voice JSONs** can be generated with the Supertonic Voice Builder: https://supertonic.supertone.ai/voice-builder
- **Sentis backend** is `BackendType.CPU` by default (inspector-configurable on the prefab). `vector_estimator_fp16.sentis` is ~128 MB — GPU backends may hit texture-size limits on some platforms. Re-test cross-platform if you change it.
- **Diffusion loop is the cost driver**: `totalStep` (default 8) directly controls latency. Lower it to 6 if first-syllable latency in Vivox feels too long; the warmup amortizes the *first* call only.
- **Vivox audio injection is WAV-file based**, not a streaming buffer. Each `Synthesize` call rewrites the same `SupertonicAudio.wav` and re-calls `StartAudioInjection`. Concurrent/rapid calls will clobber each other — `SupertonicTtsManager.Synthesize` drops requests received while `busy` is true.
- **Build size**: the four `.sentis` models total ~200 MB in StreamingAssets. Mobile builds will be heavy — consider Addressables before shipping to mobile.
- **No native plugins**: unlike the Piper-era code, no `libespeak-ng` / `Assets/Plugins/{Windows,macOS,Android}` is required. `Assets/Plugins/Android/AndroidManifest.xml` is the Unity-generated default; safe to leave or regenerate.
- **StreamingAssets I/O**: `SupertonicTtsManager` switches between `File.IO` (Editor/Standalone) and `UnityWebRequest` (Android/WebGL) via `CanReadStreamingAssetsWithFileApi()`. Don't bypass this — the file API doesn't reach the APK/WebGL StreamingAssets layout.
- **Prefab GUID `b8ead6ed17da7fd4dbc5fe1e4bfbf7c2`** is `SupertonicTtsManager.prefab` (formerly `PiperManager.prefab`, renamed during the swap). MainScene references this prefab by GUID, so don't recreate it from scratch — edit in place.
- **Script GUID `8b9064298c6f4d04a319303f4eef91ed`** is `SupertonicTtsManager.cs`. The prefab references it directly; if you rename the script file, keep the `.meta` file with it.
