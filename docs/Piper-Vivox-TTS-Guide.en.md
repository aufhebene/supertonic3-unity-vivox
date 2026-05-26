# Extending Vivox TTS to Multiple Languages with Sentis + Piper

This document explains how to bypass the English-only limitation of Unity's Vivox SDK built-in TTS by using **Sentis** + **Piper** to broadcast Korean, Japanese, Spanish, and 36+ other languages into a Vivox voice channel. The repository you are reading (`piper-unity-vivox`) is a working reference implementation.

> This sample is built on top of Sky Kim's [`piper-unity`](https://github.com/skykim/piper-unity) project (under the equivalent of an MIT-style license), with a Vivox transmission layer added on top. The guide below contains everything you need to integrate the system without having to consult the Sky Kim repository separately.

---

## 1. Why this combination

| Aspect | Vivox built-in TTS | Piper (Sentis) |
|--------|--------------------|----|
| Languages | English only | 39 languages (plus 100+ via eSpeak-ng phonemization) |
| Where it runs | Cloud | On-device |
| Latency | Network round-trip | Sub-second |
| Model size | – | 20–60 MB per voice |
| Custom voices | Not supported | VITS-based fine-tuning possible |
| Cost | Per-call billing | Free (MIT / GPL-3.0) |

In short: **if you ship a global product that needs multilingual voice chat, or you want unique character voices,** putting Piper in front of Vivox is the most practical path.

---

## 2. How it works

```
[User-typed text]
        │
        ▼
 ESpeakTokenizer ──► eSpeak-ng (native)        ── IPA phonemes
        │
        ▼
 PiperManager   ──► Sentis Worker (.sentis VITS model)  ── float[] PCM
        │
        ▼
 OnAudioDataGenerated event
        │
        ▼
 VivoxVoiceManager ──► writes WAV to persistentDataPath
        │
        ▼
 VivoxService.StartAudioInjection(filePath)
        │
        ▼
 [Real-time playback to all participants in the channel]
```

The crucial detail is that **the Vivox SDK does not accept raw `float[]` buffers, so we route the audio through a WAV file** consumed by `StartAudioInjection`.

---

## 3. Piper voice model anatomy (read this first)

A Piper voice consists of **three kinds of files**. Once you understand this layout, every later step becomes obvious.

### 3.1 Naming convention

```
<lang>_<COUNTRY>-<voiceName>-<quality>.<ext>
e.g.  en_US-amy-medium.sentis
      ko_KR-yourvoice-medium.onnx.json
```

- **lang**: ISO 639-1 (en, ko, ja, fr, es, …)
- **COUNTRY**: ISO 3166-1 alpha-2 (US, KR, JP, FR, ES, …)
- **quality**: `x_low` (16 kHz) / `low` (16 kHz) / `medium` (22.05 kHz) / `high` (22.05 kHz). Higher quality = larger model and heavier inference.

### 3.2 Role of each file

| File | Size | What it is | Where it lives in this sample |
|------|------|------------|--------------------------------|
| `<voice>.onnx` | 20–60 MB | The original ONNX VITS model published by Piper | (intermediate, not shipped) |
| `<voice>.sentis` | 20–60 MB | Unity Sentis serialized form. Loads quickly at runtime | `Assets/StreamingAssets/Models/` |
| `<voice>.onnx.json` | tens of KB | Phoneme→ID map, sample rate, inference scales | `Assets/StreamingAssets/Models/` |

**The `.onnx` file is not used at runtime.** Sentis imports it once and we ship only the converted `.sentis` (see §4.5).

### 3.3 What the `.onnx.json` contains (real excerpt)

`Assets/StreamingAssets/Models/en_US-amy-medium.onnx.json`:

```json
{
  "audio": {
    "sample_rate": 22050,        // output PCM sample rate
    "quality": "medium"
  },
  "espeak": {
    "voice": "en-us"             // voice name passed to eSpeak-ng
  },
  "inference": {
    "noise_scale": 0.667,        // VITS inference parameters
    "length_scale": 1,           // 1.0 = original speed, >1 = slower
    "noise_w": 0.8
  },
  "phoneme_type": "espeak",
  "phoneme_id_map": {            // phoneme → integer token mapping
    "_": [0],
    " ": [3],
    "a": [...],
    ...
  }
}
```

`ESpeakTokenizer.cs` reads this JSON at runtime to determine:
1. Which voice to load into eSpeak-ng (`espeak.voice`)
2. How to translate phoneme strings into model input tokens (`phoneme_id_map`)
3. Which inference scales to feed the model (`inference.*`)
4. The sample rate of the output PCM (`audio.sample_rate`)

**Each model has its own JSON, so `.sentis` and `.onnx.json` must always be shipped as a pair.**

---

## 4. Prerequisites and integration

### 4.1 Environment

- Unity **6000.2.0b9** or newer (this sample's baseline; Sky Kim's original works on 6000.0.50f1)
- Sentis (Inference Engine) `com.unity.ai.inference` 2.2.1+ (this sample uses 2.3.0)
- Vivox `com.unity.services.vivox` 16.6.2+
- Vivox credentials (App ID / Issuer / Domain / Server) — issued via the Vivox Developer Portal or Unity Dashboard

`Packages/manifest.json`:

```json
"com.unity.ai.inference": "2.3.0",
"com.unity.services.vivox": "16.6.2",
"com.unity.services.authentication": "3.4.1"
```

### 4.2 eSpeak-ng native libraries

Piper's text→phoneme stage relies on the C library eSpeak-ng. Drop **eSpeak-ng 1.5.2** binaries into:

```
Assets/Plugins/
├── Windows/x64/libespeak-ng.dll
├── macOS/x64/libespeak-ng.dylib   (+ libespeak-ng.1.dylib symlink/copy)
└── Android/libs/arm64-v8a/libespeak-ng.so
```

How to obtain them:
- **Already included in this repository's `Assets/Plugins/` directory** (easiest path)
- Or download eSpeak-ng 1.5.2 from https://github.com/espeak-ng/espeak-ng/releases and extract the platform-specific dylib/dll/so
- On macOS, Gatekeeper may quarantine the dylib — clear it once with `xattr -d com.apple.quarantine libespeak-ng.dylib`

In the Unity Editor, open each `.meta` file (Plugin Inspector → Platform settings) and confirm the correct OS/CPU is checked.

### 4.3 eSpeak-ng data folder

eSpeak-ng needs a separate ~12 MB data directory containing per-language pronunciation rules and dictionaries.

```
Assets/StreamingAssets/
├── espeak-ng-data/             # used directly in Editor / Standalone
└── espeak-ng-data.zip          # unzipped to persistentDataPath on first Android run
```

How to obtain:
- **Already included in this repository's `Assets/StreamingAssets/`**
- Or install eSpeak-ng on your system and copy `/usr/share/espeak-ng-data` (Linux/macOS) or `C:\Program Files\eSpeak NG\espeak-ng-data` (Windows)

> **Android note**: `PiperManager.InitializePiper()` automatically extracts `StreamingAssets/espeak-ng-data.zip` to `Application.persistentDataPath` on the first launch and skips this step on subsequent launches. The zip must be shipped with the build.

### 4.4 Acquiring Piper voice models

Two options:

**A. Use pre-converted `.sentis` files (recommended, fastest)**

This repository's `Assets/StreamingAssets/Models/` already ships with five languages:

| File | Language | Quality | Sample rate |
|------|----------|---------|-------------|
| `en_US-amy-medium.sentis` | English (US) | medium | 22050 Hz |
| `es_ES-carlfm-x_low.sentis` | Spanish | x_low | 16000 Hz |
| `fr_FR-siwis-medium.sentis` | French | medium | 22050 Hz |
| `hi_IN-pratham-medium.sentis` | Hindi | medium | 22050 Hz |
| `zh_CN-huayan-medium.sentis` | Chinese (Simplified) | medium | 22050 Hz |

For more languages, download from Sky Kim's converted collection:
- Converted model collection: https://huggingface.co/skykim/piper-unity
- Bulk StreamingAssets bundle (Google Drive link): see Sky Kim's repository README

**B. Convert from the original `.onnx` yourself**

Original ONNX models live at the Piper home repository:

- Listen to samples: https://rhasspy.github.io/piper-samples/
- Full voice catalog: https://github.com/rhasspy/piper/blob/master/VOICES.md
- Direct downloads: https://huggingface.co/rhasspy/piper-voices

Each voice ships as `<name>.onnx` + `<name>.onnx.json`.

> ⚠️ A few models are incompatible with the current Sentis version due to unsupported ops. Start with the `medium` tier — it is the most reliably importable.

### 4.5 ONNX → Sentis conversion

Sentis can consume ONNX in two ways.

**Option 1: Unity Editor auto-import (simplest)**

1. Drop `<name>.onnx` and `<name>.onnx.json` somewhere under `Assets/` (e.g. `Assets/Models/`)
2. Unity imports it as a `ModelAsset` — the Inspector shows the node graph
3. Use it from code via `ModelLoader.Load(modelAsset)`

In this mode the build ships the ONNX as-is, parsed by Sentis at load time. Slightly slower to load, but no conversion step.

**Option 2: Pre-serialize to `.sentis` (recommended — what this sample does)**

Faster runtime loading and easier dynamic model swapping because files live on disk. A one-shot Editor menu item handles it:

```csharp
using UnityEditor;
using Unity.InferenceEngine;

public static class PiperModelConverter
{
    [MenuItem("Tools/Piper/Convert ONNX to Sentis")]
    public static void Convert()
    {
        // 1. Place the ONNX under Assets/ so it imports as a ModelAsset
        var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(
            "Assets/Models/en_US-amy-medium.onnx");

        // 2. Materialize as a Model object
        var model = ModelLoader.Load(asset);

        // 3. Write it out as a .sentis binary
        var outPath = "Assets/StreamingAssets/Models/en_US-amy-medium.sentis";
        ModelWriter.Save(outPath, model);

        AssetDatabase.Refresh();
    }
}
```

After conversion:
- Place `<voice>.sentis` under `Assets/StreamingAssets/Models/`
- Place `<voice>.onnx.json` **next to it under the same name** (`PiperManager` loads them as a pair)
- The original `.onnx` can be excluded from the build (move it out of `Assets/` or `.gitignore` it)

### 4.6 Wiring it together

**A. Set up PiperManager** — Attach `PiperManager` (`Assets/Scripts/Piper/PiperManager.cs`) and `ESpeakTokenizer` to the same GameObject. `Start()` automatically:

1. Picks the eSpeak-ng data path (Android → `persistentDataPath`, otherwise → `StreamingAssets`)
2. Calls `ESpeakNG.espeak_Initialize()`
3. Emits synthesis output through the `OnAudioDataGenerated(float[] audioData, int sampleRate)` event

**B. Vivox subscriber** — The key wiring inside `VivoxVoiceManager` (`Assets/Scripts/ChatChannelSample/Managers/VivoxVoiceManager.cs`):

```csharp
// inside Awake()
PiperManager.Instance.OnAudioDataGenerated += OnAudioDataGenerated;

void OnAudioDataGenerated(float[] audioData, int sampleRate)
{
    if (!usePiper) return;

    var filePath = Path.Combine(Application.persistentDataPath, "PiperAudio.wav");
    SaveToWav(filePath, audioData, sampleRate);            // 16-bit PCM mono WAV
    VivoxService.Instance.StartAudioInjection(filePath);   // inject into channel
}

public void TextToSpeechSendMessage(string message)
{
    if (usePiper)
        PiperManager.Instance.Synthesize(message);                    // multilingual path
    else
        VivoxService.Instance.TextToSpeechSendMessage(                 // English fallback
            message, TextToSpeechMessageType.RemoteTransmissionWithLocalPlayback);
}
```

**C. Runtime model-switching UI** — `ModelSelector.cs` scans `StreamingAssets/Models/*.sentis`, populates a TMP_Dropdown, and calls `PiperManager.Instance.LoadNewModel(name)` on selection. Drop a new model into the folder and it appears in the dropdown — no code changes required.

---

## 5. Adding a new voice (summary)

If you already have a `.sentis`:

1. Copy `<name>.sentis` + `<name>.onnx.json` into `Assets/StreamingAssets/Models/`
2. Run the project — the `ModelSelector` dropdown picks it up automatically
3. Select it and start synthesizing in the new language

If you only have the original `.onnx`, run the §4.5 conversion first, then follow the steps above.

---

## 6. Custom voice training (optional)

To build a voice exclusive to a game character:

1. **Prepare data** — wav clips (22.05 kHz mono recommended) plus a `metadata.csv` mapping filename to transcript. LJ Speech format works well:
   ```
   001.wav|Hello, I am Garen, the Might of Demacia.
   002.wav|Justice will be my judgment!
   ```
   Quality target: **~24 hours / 13,000 clips** (LJ Speech baseline). For a short character voice, fine-tuning on 1–2 hours can be enough.

2. **Train on Google Colab** — Use Piper's official training scripts (https://github.com/rhasspy/piper/blob/master/TRAINING.md). A T4-class GPU or better is required. Fine-tuning takes hours to days.

3. **Convert and ship** — Take the trained `.onnx` + `.onnx.json` through §4.5 to produce a `.sentis`, then drop into `StreamingAssets/Models/`.

---

## 7. Known limitations & troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| First synthesis stutters / is slow | Sentis Worker warm-up cost. This sample calls `_WarmupModel()` automatically right after model load |
| eSpeak fails to initialize on Android | `espeak-ng-data.zip` missing from StreamingAssets, or persistentDataPath permission issue. Inspect first-run logs |
| dylib fails to load on macOS | Gatekeeper quarantine. Run `xattr -d com.apple.quarantine libespeak-ng.dylib` |
| Vivox playback sounds choppy | `StartAudioInjection` is **file-based**, so longer sentences accumulate latency. The `SynthesizeCoroutine` already chunks on punctuation — keep that behavior |
| iOS / WebGL not supported | The Piper Unity port currently targets Win/macOS/Android only. iOS/Web are on the roadmap |
| Cannot push raw `float[]` directly | Vivox SDK limitation. This sample uses the WAV-file workaround. A raw-buffer API has been proposed to the Vivox team |
| A specific model fails to import | Op compatibility with the current Sentis version. Try a different quality tier (e.g. high → medium) |
| Korean voice sounds unnatural | The pre-trained `ko_KR` voices are weaker. Consider training your own (§6) or evaluating Coqui-TTS as an alternative |

---

## 8. Going further

- **Pair with Whisper (STT)** — Sentis can also run OpenAI Whisper-tiny (~230 MB) on-device. Voice in → Whisper transcribes → LLM/translation → Piper synthesizes back into the channel in any of the 39 languages. Official sample: https://huggingface.co/unity/inference-engine-whisper-tiny
- **Per-character voice routing** — Use multiple `PiperManager` instances or pool models so each NPC speaks with a distinct voice
- **Lower-latency streaming** — If/when Vivox exposes a raw `float[]` push API, you can swap the WAV-file path inside `OnAudioDataGenerated` for a single-line streaming call

---

## 9. Sources & licenses

This guide is built on the following work.

- **Sky Kim, `piper-unity`** — the Unity port of Piper (eSpeak-ng native bindings, Sentis integration, voice model collection). https://github.com/skykim/piper-unity / https://huggingface.co/skykim/piper-unity
- **Rhasspy, `piper`** — the upstream Piper project (training scripts, voice catalog). https://github.com/rhasspy/piper / https://huggingface.co/rhasspy/piper-voices
- **eSpeak-ng** — multilingual phonemization engine. https://github.com/espeak-ng/espeak-ng (GPL-3.0)
- **VITS** — Conditional VAE with Adversarial Learning for End-to-End TTS, Kakao 2021
- **Unity Sentis (Inference Engine)** — https://docs.unity3d.com/Packages/com.unity.ai.inference@latest
- **Vivox Unity SDK** — https://docs.vivox.com/v5/general/unity/

License note: Piper is MIT (older versions) / GPL-3.0 (newer versions); eSpeak-ng is GPL-3.0. When shipping commercially, double-check the version and license combination of every dependency you bundle.
