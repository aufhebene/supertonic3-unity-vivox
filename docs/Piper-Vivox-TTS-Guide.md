# Sentis + Piper로 Vivox TTS 다국어 확장하기

이 문서는 Unity Vivox SDK의 기본 TTS(영어 전용) 한계를 **Sentis** + **Piper** 조합으로 우회하여, 한국어·일본어·스페인어 등 39개 언어의 음성을 Vivox 보이스 채널로 송출하는 방법을 설명합니다. 본 저장소(`piper-unity-vivox`)가 그대로 동작 가능한 레퍼런스 샘플입니다.

> 본 샘플은 Sky Kim의 [`piper-unity`](https://github.com/skykim/piper-unity) 프로젝트(MIT 라이선스에 준함)를 토대로, Vivox 송출 레이어를 추가한 것입니다. 아래 가이드는 Sky Kim 저장소를 별도로 참조하지 않아도 통합이 끝나도록 모든 정보를 본문에 포함합니다.

---

## 1. 왜 이 조합인가

| 항목 | Vivox 내장 TTS | Piper (Sentis) |
|------|----------------|----------------|
| 지원 언어 | 영어만 | 39개 언어 (eSpeak-ng로 100+ 언어 음소화) |
| 처리 위치 | 클라우드 | 온디바이스 |
| 지연 | 네트워크 왕복 발생 | 수백 ms 이내 |
| 모델 크기 | - | 20~60 MB / 음성 |
| 커스텀 보이스 | 불가 | VITS 기반 학습 가능 |
| 비용 | 호출량 기반 | 무료 (MIT / GPL-3.0) |

요약: **글로벌 서비스에서 다국어 음성 채팅이 필요하거나, 캐릭터 전용 보이스가 필요하다면** Piper를 Vivox 앞단에 붙이는 것이 가장 현실적인 방법입니다.

---

## 2. 동작 원리

```
[사용자 입력 텍스트]
        │
        ▼
 ESpeakTokenizer ──► eSpeak-ng (네이티브)  ── 음소(IPA) 변환
        │
        ▼
 PiperManager  ──► Sentis Worker (.sentis VITS 모델)  ── float[] PCM 생성
        │
        ▼
 OnAudioDataGenerated 이벤트
        │
        ▼
 VivoxVoiceManager  ──► WAV 파일로 저장 (persistentDataPath)
        │
        ▼
 VivoxService.StartAudioInjection(filePath)
        │
        ▼
 [채널의 모든 참여자에게 실시간 송출]
```

핵심은 **Vivox SDK가 raw `float[]` 버퍼를 직접 받지 못하므로, WAV 파일로 우회 주입한다**는 점입니다 (`StartAudioInjection` API 사용).

---

## 3. Piper 음성 모델 파일 구조 (필수 이해)

Piper 음성 하나는 **세 종류의 파일**로 구성됩니다. 이 구조를 알면 다음 장의 작업이 모두 자명해집니다.

### 3.1 파일 명명 규칙

```
<언어코드>_<국가코드>-<보이스이름>-<품질>.<확장자>
예: en_US-amy-medium.sentis
    ko_KR-yourvoice-medium.onnx.json
```

- **언어코드**: ISO 639-1 (en, ko, ja, fr, es, ...)
- **국가코드**: ISO 3166-1 alpha-2 (US, KR, JP, FR, ES, ...)
- **품질 등급**: `x_low` (16kHz) / `low` (16kHz) / `medium` (22.05kHz) / `high` (22.05kHz)
  - 품질이 높을수록 모델 크기와 추론 비용 증가

### 3.2 세 파일의 역할

| 파일 | 크기 | 정체 | 본 샘플에서의 위치 |
|------|------|------|---------------------|
| `<voice>.onnx` | 20~60 MB | Piper가 학습/배포하는 원본 ONNX VITS 모델 | (변환 전 임시) |
| `<voice>.sentis` | 20~60 MB | Unity Sentis가 직렬화한 형식. 빠르게 로드됨 | `Assets/StreamingAssets/Models/` |
| `<voice>.onnx.json` | 수십 KB | 음소→ID 매핑·샘플레이트·추론 스케일 등 메타 | `Assets/StreamingAssets/Models/` |

**`.onnx` 파일은 런타임에 직접 사용하지 않습니다.** Sentis가 `.onnx`를 임포트해 `.sentis`로 변환한 결과만 빌드에 포함됩니다 (4.5절 참조).

### 3.3 `.onnx.json`의 내용 (실제 파일 발췌)

`Assets/StreamingAssets/Models/en_US-amy-medium.onnx.json`:

```json
{
  "audio": {
    "sample_rate": 22050,        // 출력 PCM 샘플레이트
    "quality": "medium"
  },
  "espeak": {
    "voice": "en-us"             // eSpeak-ng에 넘길 voice 이름
  },
  "inference": {
    "noise_scale": 0.667,        // VITS 추론 파라미터
    "length_scale": 1,           // 1.0 = 원래 속도, >1 = 더 느리게
    "noise_w": 0.8
  },
  "phoneme_type": "espeak",
  "phoneme_id_map": {            // 음소 → 정수 토큰 매핑
    "_": [0],
    " ": [3],
    "a": [...],
    ...
  }
}
```

`ESpeakTokenizer.cs`가 이 JSON을 런타임에 읽어서:
1. eSpeak-ng에 어떤 voice를 설정할지 (`espeak.voice`)
2. 음소 문자열을 모델 입력 토큰 배열로 변환할 때 어떤 ID를 쓸지 (`phoneme_id_map`)
3. 모델에 어떤 inference scale을 넘길지 (`inference.*`)
4. 출력 PCM의 샘플레이트가 무엇인지 (`audio.sample_rate`)

를 모두 결정합니다. **모델마다 이 JSON이 다르므로 `.sentis`와 `.onnx.json`은 항상 짝으로 배포해야 합니다.**

---

## 4. 사전 준비물과 통합 절차

### 4.1 환경

- Unity **6000.2.0b9** 이상 (본 샘플 기준; Sky Kim 원본은 6000.0.50f1로도 동작)
- Sentis (Inference Engine) `com.unity.ai.inference` 2.2.1+ (본 샘플은 2.3.0)
- Vivox `com.unity.services.vivox` 16.6.2+
- Vivox 자격증명 (App ID / Issuer / Domain / Server) — Vivox Developer Portal 또는 Unity Dashboard에서 발급

`Packages/manifest.json`:

```json
"com.unity.ai.inference": "2.3.0",
"com.unity.services.vivox": "16.6.2",
"com.unity.services.authentication": "3.4.1"
```

### 4.2 eSpeak-ng 네이티브 라이브러리

Piper의 텍스트→음소 변환은 C로 작성된 eSpeak-ng 라이브러리에 의존합니다. **eSpeak-ng 1.5.2** 빌드 산출물을 다음 경로에 배치합니다:

```
Assets/Plugins/
├── Windows/x64/libespeak-ng.dll
├── macOS/x64/libespeak-ng.dylib   (+ libespeak-ng.1.dylib 심볼릭 사본)
└── Android/libs/arm64-v8a/libespeak-ng.so
```

획득 방법:
- **본 샘플 저장소의 `Assets/Plugins/` 디렉터리에 이미 포함**되어 있음 (가장 쉬움)
- 또는 https://github.com/espeak-ng/espeak-ng/releases 에서 1.5.2 릴리스 다운로드 후 각 플랫폼 dylib/dll/so 추출
- macOS는 보안 게이트키퍼로 차단될 수 있으므로 `xattr -d com.apple.quarantine libespeak-ng.dylib` 한 번 필요

각 라이브러리의 `.meta` 파일에서 **Plugin Inspector → Platform settings**가 올바른 OS/CPU에 체크되어 있는지 확인합니다.

### 4.3 eSpeak-ng 데이터 폴더

eSpeak-ng는 언어별 발음 규칙·사전을 별도 데이터 디렉터리로 갖습니다. **약 12 MB**의 `espeak-ng-data/` 폴더가 필요합니다.

```
Assets/StreamingAssets/
├── espeak-ng-data/             # Editor / Standalone에서 직접 참조
└── espeak-ng-data.zip          # Android 첫 실행 시 persistentDataPath로 풀어 사용
```

획득 방법:
- **본 샘플 저장소의 `Assets/StreamingAssets/`에 이미 포함**되어 있음
- 또는 eSpeak-ng를 시스템에 설치하면 `/usr/share/espeak-ng-data` (Linux/macOS) / `C:\Program Files\eSpeak NG\espeak-ng-data` (Windows)에 생성되며, 이를 그대로 복사

> **Android 처리**: `PiperManager.InitializePiper()`가 첫 실행 시 `StreamingAssets/espeak-ng-data.zip`을 `Application.persistentDataPath`로 자동 압축 해제합니다. 두 번째 실행부터는 이 단계를 건너뜁니다. zip은 반드시 함께 배포하세요.

### 4.4 Piper 음성 모델 획득

Piper 모델은 두 경로 중 하나로 얻을 수 있습니다.

**A. 이미 변환된 `.sentis` 파일 사용 (권장, 가장 빠름)**

본 샘플 저장소의 `Assets/StreamingAssets/Models/`에 다음 5개 언어가 이미 포함:

| 파일 | 언어 | 품질 | 샘플레이트 |
|------|------|------|-----------|
| `en_US-amy-medium.sentis` | 영어 (미국) | medium | 22050 Hz |
| `es_ES-carlfm-x_low.sentis` | 스페인어 | x_low | 16000 Hz |
| `fr_FR-siwis-medium.sentis` | 프랑스어 | medium | 22050 Hz |
| `hi_IN-pratham-medium.sentis` | 힌디어 | medium | 22050 Hz |
| `zh_CN-huayan-medium.sentis` | 중국어 (간체) | medium | 22050 Hz |

추가 언어가 필요하면 Sky Kim이 변환·공유한 컬렉션에서 다운로드:
- 변환된 모델 컬렉션: https://huggingface.co/skykim/piper-unity
- StreamingAssets 일괄 번들 (Sky Kim 배포 Google Drive 링크): Sky Kim 저장소 README 참조

**B. 원본 `.onnx`에서 직접 변환**

원본 ONNX 모델은 Piper 본가에서 받습니다.

- 음성 샘플 청취: https://rhasspy.github.io/piper-samples/
- 전체 음성 카탈로그: https://github.com/rhasspy/piper/blob/master/VOICES.md
- 직접 다운로드: https://huggingface.co/rhasspy/piper-voices

각 음성마다 `<name>.onnx` + `<name>.onnx.json` 두 파일이 함께 제공됩니다.

> ⚠️ 일부 모델은 현재 Sentis 버전과 op 호환성 문제로 임포트가 실패할 수 있습니다. medium 등급부터 시도하는 것을 권장합니다.

### 4.5 ONNX → Sentis 변환

Sentis는 두 가지 방식으로 ONNX를 받습니다.

**방식 1: Unity Editor 자동 임포트 (가장 간단)**

1. `<name>.onnx`와 `<name>.onnx.json`을 `Assets/` 어딘가에 (예: `Assets/Models/`) 드롭
2. Unity가 자동으로 `ModelAsset`으로 임포트 → 인스펙터에 노드 그래프가 표시됨
3. 코드에서 `ModelLoader.Load(modelAsset)` 으로 사용 가능

이 경우 빌드에는 ONNX가 그대로 포함되며 런타임에 Sentis가 파싱합니다. 로딩이 약간 느리지만 변환 단계가 없습니다.

**방식 2: `.sentis`로 사전 직렬화 (권장 — 본 샘플 방식)**

런타임 로딩이 빠르고 `StreamingAssets`에서 파일 단위로 다룰 수 있어 동적 모델 교체가 쉬워집니다. Editor 스크립트 한 번 실행으로 변환:

```csharp
using UnityEditor;
using Unity.InferenceEngine;

public static class PiperModelConverter
{
    [MenuItem("Tools/Piper/Convert ONNX to Sentis")]
    public static void Convert()
    {
        // 1. ONNX를 Assets 어딘가에 두면 ModelAsset으로 임포트됨
        var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(
            "Assets/Models/en_US-amy-medium.onnx");

        // 2. Model 객체로 변환
        var model = ModelLoader.Load(asset);

        // 3. .sentis 바이너리로 직렬화
        var outPath = "Assets/StreamingAssets/Models/en_US-amy-medium.sentis";
        ModelWriter.Save(outPath, model);

        AssetDatabase.Refresh();
    }
}
```

변환 후:
- `<voice>.sentis` → `Assets/StreamingAssets/Models/`에 배치
- `<voice>.onnx.json` → **같은 위치에 같은 이름으로** 함께 배치 (`PiperManager`가 짝을 이뤄 로드)
- 원본 `.onnx`는 빌드에서 제외 가능 (Assets 밖으로 이동하거나 `.gitignore`)

### 4.6 통합 코드 결선

**A. PiperManager 셋업** — `PiperManager` (`Assets/Scripts/Piper/PiperManager.cs`)와 `ESpeakTokenizer`를 한 GameObject에 부착. `Start()`에서 자동으로:

1. eSpeak-ng 데이터 경로 결정 (Android는 persistentDataPath, 그 외는 StreamingAssets)
2. `ESpeakNG.espeak_Initialize()` 호출
3. 합성 결과는 `OnAudioDataGenerated(float[] audioData, int sampleRate)` 이벤트로 발행

**B. Vivox 구독자** — `VivoxVoiceManager` (`Assets/Scripts/ChatChannelSample/Managers/VivoxVoiceManager.cs`)의 핵심 결선:

```csharp
// Awake() 내부
PiperManager.Instance.OnAudioDataGenerated += OnAudioDataGenerated;

void OnAudioDataGenerated(float[] audioData, int sampleRate)
{
    if (!usePiper) return;

    var filePath = Path.Combine(Application.persistentDataPath, "PiperAudio.wav");
    SaveToWav(filePath, audioData, sampleRate);          // 16-bit PCM mono WAV
    VivoxService.Instance.StartAudioInjection(filePath); // Vivox 채널로 주입
}

public void TextToSpeechSendMessage(string message)
{
    if (usePiper)
        PiperManager.Instance.Synthesize(message);                     // 다국어 경로
    else
        VivoxService.Instance.TextToSpeechSendMessage(                  // 영어 fallback
            message, TextToSpeechMessageType.RemoteTransmissionWithLocalPlayback);
}
```

**C. 런타임 모델 전환 UI** — `ModelSelector.cs`가 `StreamingAssets/Models/*.sentis`를 스캔해서 TMP_Dropdown에 표시하고, 선택 시 `PiperManager.Instance.LoadNewModel(name)` 을 호출. 새 모델을 폴더에 드롭만 하면 코드 수정 없이 인식됩니다.

---

## 5. 새 음성 모델 추가하기 (요약)

이미 변환된 `.sentis`가 있다면:

1. `<name>.sentis` + `<name>.onnx.json` 두 파일을 `Assets/StreamingAssets/Models/`에 복사
2. 실행하면 `ModelSelector` 드롭다운에 자동 등장
3. 선택 → 해당 언어로 합성 시작

원본 `.onnx`만 있다면 4.5절 변환 → 위 절차.

---

## 6. 커스텀 보이스 학습 (선택)

게임 캐릭터 전용 음성을 만들고 싶다면:

1. **데이터 준비** — `wav` 파일들 (22.05 kHz mono 권장)과 `metadata.csv`(`파일명|텍스트` 매핑) 작성. LJ Speech 포맷 권장.
   ```
   001.wav|안녕하세요, 데마시아의 가렌입니다.
   002.wav|정의가 곧 나의 심판이다.
   ```
   품질 확보 권장량: **24시간 / 13,000 클립** 수준 (LJ Speech 기준). 짧은 캐릭터 보이스라면 1~2시간으로도 fine-tuning 가능.

2. **Google Colab에서 학습** — Piper 공식 학습 스크립트 (https://github.com/rhasspy/piper/blob/master/TRAINING.md) 사용. GPU(T4 이상) 필요. fine-tuning 기준 수 시간~수 일.

3. **변환 후 배포** — 학습 산출물 `.onnx` + `.onnx.json`을 4.5절 절차로 `.sentis`로 변환 → `StreamingAssets/Models/`에 배치.

---

## 7. 알려진 제약과 트러블슈팅

| 증상 | 원인 / 대응 |
|------|-------------|
| 첫 합성 시 끊김 / 지연 | Sentis Worker 워밍업 비용. 본 샘플은 모델 로드 직후 `_WarmupModel()` 자동 호출 |
| Android에서 eSpeak 초기화 실패 | `espeak-ng-data.zip`이 StreamingAssets에 없거나, persistentDataPath 권한 문제. 첫 실행 로그 확인 |
| macOS에서 dylib 로드 실패 | Gatekeeper 격리. `xattr -d com.apple.quarantine libespeak-ng.dylib` 실행 |
| Vivox 송출이 끊겨서 들림 | `StartAudioInjection`은 **파일 단위**라서 문장이 길수록 지연 누적. 문장을 구두점으로 청크 분할(`SynthesizeCoroutine`이 이미 수행) |
| iOS / WebGL 미지원 | Piper Unity 포팅이 현재 Win/macOS/Android만 지원. iOS/Web는 로드맵 |
| float[] 직접 송출 불가 | Vivox SDK의 한계. 본 샘플은 WAV 파일 우회. raw 버퍼 API 추가는 Vivox 측에 제안 단계 |
| 특정 모델 임포트 실패 | Sentis와의 op 호환성 문제. 다른 품질 등급(예: high → medium)으로 시도 |
| 한국어 음성이 부자연스럽다 | `ko_KR` 사전학습 음성 품질이 낮은 편. 자체 학습(6장) 또는 Coqui-TTS 등 대체 검토 |

---

## 8. 더 나아가기

- **Whisper(STT) 결합** — Sentis로 OpenAI Whisper-tiny(약 230 MB)도 온디바이스 실행 가능. 음성 입력 → Whisper로 텍스트 → LLM/번역 → Piper로 다국어 송출 파이프라인 구축 가능. 공식 샘플: https://huggingface.co/unity/inference-engine-whisper-tiny
- **캐릭터별 보이스 라우팅** — `PiperManager`를 멀티 인스턴스화하거나 모델 풀링하여 NPC마다 다른 보이스 사용
- **저지연 스트리밍** — WAV 파일 기반 주입 대신 Vivox에 raw float[] 푸시 API가 추가되면 본 샘플의 `OnAudioDataGenerated` 지점에서 한 줄 교체로 전환 가능

---

## 9. 출처와 라이선스

본 가이드는 다음 자료를 토대로 작성되었습니다.

- **Sky Kim, `piper-unity`** — Piper의 Unity 포팅 원본 (eSpeak-ng 네이티브 바인딩, Sentis 통합, 음성 모델 컬렉션). https://github.com/skykim/piper-unity / https://huggingface.co/skykim/piper-unity
- **Rhasspy, `piper`** — Piper 본가 (학습 스크립트, 음성 카탈로그). https://github.com/rhasspy/piper / https://huggingface.co/rhasspy/piper-voices
- **eSpeak-ng** — 다국어 음소화 엔진. https://github.com/espeak-ng/espeak-ng (GPL-3.0)
- **VITS** — Conditional VAE with Adversarial Learning for End-to-End TTS, Kakao 2021
- **Unity Sentis (Inference Engine)** — https://docs.unity3d.com/Packages/com.unity.ai.inference@latest
- **Vivox Unity SDK** — https://docs.vivox.com/v5/general/unity/

라이선스: Piper는 MIT(구버전) / GPL-3.0(신버전), eSpeak-ng는 GPL-3.0이므로 상업 배포 시 의존 라이브러리 버전과 라이선스 조합을 반드시 확인하세요.
