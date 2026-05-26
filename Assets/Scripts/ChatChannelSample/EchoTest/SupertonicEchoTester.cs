using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Supertonic.Unity;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;
#if AUTH_PACKAGE_PRESENT
using Unity.Services.Authentication;
#endif

/// <summary>
/// Self-contained Vivox echo channel tester for Supertonic TTS injection.
///
/// Joins a Vivox echo channel that loops your transmitted audio back to your own speakers.
/// This lets you verify the end-to-end TTS-to-Vivox path WITHOUT needing a second client:
/// if synthesis + injection + Vivox routing all work, you will hear the synthesized voice
/// echoed back at you within a couple of seconds.
///
/// Usage:
///   1. Add this component to any GameObject in a scene (no scene wiring required).
///   2. Make sure SupertonicTtsManager prefab is also in the scene (or one will be auto-created
///      by SupertonicTtsManager.Instance — but inspector defaults will not be respected then).
///   3. Vivox credentials: either set them on VivoxVoiceManager in the same scene, OR if the
///      Unity Authentication package is present, this tester will sign in anonymously.
///   4. Enter Play mode. Use the on-screen IMGUI panel (top-left) to drive the test.
/// </summary>
public class SupertonicEchoTester : MonoBehaviour
{
    [Header("Echo Channel")]
    [SerializeField] string echoChannelName = "supertonicEchoTest";

    [Header("Test Message")]
    [TextArea(2, 4)]
    [SerializeField] string testText = "Hello, this is a Supertonic test message in the echo channel.";
    [SerializeField] SupertonicLanguage testLanguage = SupertonicLanguage.en;
    [SerializeField] string testVoiceStyle = "M1";

    [Header("UI")]
    [SerializeField] bool showOverlay = true;
    [SerializeField] int overlayWidth = 420;

    string _status = "Idle";
    bool _busy;
    bool _injectionSubscribed;

    void OnGUI()
    {
        if (!showOverlay) return;

        const int pad = 10;
        GUILayout.BeginArea(new Rect(pad, pad, overlayWidth, Screen.height - pad * 2), GUI.skin.box);
        GUILayout.Label("<b>Supertonic Echo Tester</b>", RichLabel());

        GUILayout.Space(4);
        GUILayout.Label($"Vivox logged in: {(VivoxLoggedIn() ? "YES" : "no")}");
        GUILayout.Label($"Active channels: {ActiveChannelSummary()}");
        GUILayout.Label($"Status: {_status}");

        GUILayout.Space(8);
        GUILayout.Label("Test text:");
        testText = GUILayout.TextArea(testText, GUILayout.MinHeight(48));

        GUILayout.Label("Voice style (e.g. M1, F2):");
        testVoiceStyle = GUILayout.TextField(testVoiceStyle);

        GUILayout.Label($"Language: {testLanguage}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("< lang") && !_busy) testLanguage = ShiftLanguage(testLanguage, -1);
        if (GUILayout.Button("lang >") && !_busy) testLanguage = ShiftLanguage(testLanguage, +1);
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUI.enabled = !_busy;

        if (GUILayout.Button("1. Initialize + Login (anonymous)"))
            _ = InitializeAndLoginAsync();

        if (GUILayout.Button($"2. Join echo channel: {echoChannelName}"))
            _ = JoinEchoChannelAsync();

        GUILayout.Space(4);
        if (GUILayout.Button("3a. Speak via Supertonic (inject WAV)"))
            SpeakViaSupertonic();

        if (GUILayout.Button("3b. Speak via Vivox built-in TTS"))
            SpeakViaVivoxBuiltIn();

        GUILayout.Space(4);
        if (GUILayout.Button("4. Leave echo channel"))
            _ = LeaveEchoChannelAsync();

        if (GUILayout.Button("Stop audio injection"))
            StopInjection();

        GUI.enabled = true;

        GUILayout.Space(8);
        GUILayout.Label("<size=10>Echo channel loops your transmitted audio back to your own speakers. " +
                        "If you hear the synthesized voice after step 3a, the Supertonic-to-Vivox path works.</size>", RichLabel());

        GUILayout.EndArea();
    }

    void OnEnable()
    {
        if (SupertonicTtsManager.HasInstance && !_injectionSubscribed)
        {
            SubscribeInjection();
        }
    }

    void OnDisable()
    {
        UnsubscribeInjection();
    }

    void SubscribeInjection()
    {
        if (!SupertonicTtsManager.HasInstance) return;
        SupertonicTtsManager.Instance.OnAudioDataGenerated += LocalLogOnAudioGenerated;
        _injectionSubscribed = true;
    }

    void UnsubscribeInjection()
    {
        if (!_injectionSubscribed || !SupertonicTtsManager.HasInstance) return;
        SupertonicTtsManager.Instance.OnAudioDataGenerated -= LocalLogOnAudioGenerated;
        _injectionSubscribed = false;
    }

    void LocalLogOnAudioGenerated(float[] audio, int rate)
    {
        Debug.Log($"[EchoTester] Supertonic produced {audio.Length} samples @ {rate}Hz " +
                  $"({audio.Length / (float)rate:F2}s). VivoxVoiceManager.OnAudioDataGenerated " +
                  "should now write a WAV and call StartAudioInjection.");
    }

    async Task InitializeAndLoginAsync()
    {
        _busy = true;
        try
        {
            SetStatus("Initializing UnityServices...");
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();

#if AUTH_PACKAGE_PRESENT
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                SetStatus("Signing in anonymously...");
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
#endif

            SetStatus("Initializing Vivox...");
            await VivoxService.Instance.InitializeAsync();

            if (!VivoxService.Instance.IsLoggedIn)
            {
                SetStatus("Logging into Vivox...");
                await VivoxService.Instance.LoginAsync();
            }

            SetStatus("Login complete.");

            if (!_injectionSubscribed)
                SubscribeInjection();
        }
        catch (Exception ex)
        {
            SetStatus($"Login failed: {ex.Message}");
            Debug.LogError($"[EchoTester] Initialize/Login failed: {ex}");
        }
        finally { _busy = false; }
    }

    async Task JoinEchoChannelAsync()
    {
        _busy = true;
        try
        {
            if (!VivoxService.Instance.IsLoggedIn)
            {
                SetStatus("Not logged in. Run step 1 first.");
                return;
            }

            SetStatus($"Joining echo channel '{echoChannelName}'...");
            await VivoxService.Instance.JoinEchoChannelAsync(echoChannelName, ChatCapability.AudioOnly);
            SetStatus($"Joined echo channel. You should hear yourself when you speak or inject audio.");
        }
        catch (Exception ex)
        {
            SetStatus($"Join failed: {ex.Message}");
            Debug.LogError($"[EchoTester] Join echo channel failed: {ex}");
        }
        finally { _busy = false; }
    }

    void SpeakViaSupertonic()
    {
        if (string.IsNullOrWhiteSpace(testText))
        {
            SetStatus("Test text is empty.");
            return;
        }

        if (!VivoxService.Instance.IsLoggedIn)
        {
            SetStatus("Not logged in. Run step 1 first.");
            return;
        }

        var channelCount = VivoxService.Instance.ActiveChannels?.Count ?? 0;
        if (channelCount == 0)
        {
            SetStatus("No active channel. Run step 2 first.");
            return;
        }

        SupertonicTtsManager.Instance.SetLanguage(testLanguage);
        SupertonicTtsManager.Instance.LoadVoiceStyle(testVoiceStyle);

        if (!_injectionSubscribed) SubscribeInjection();

        SetStatus($"Synthesizing '{Truncate(testText, 32)}' via Supertonic...");
        SupertonicTtsManager.Instance.Synthesize(testText);
    }

    void SpeakViaVivoxBuiltIn()
    {
        if (string.IsNullOrWhiteSpace(testText))
        {
            SetStatus("Test text is empty.");
            return;
        }

        try
        {
            VivoxService.Instance.TextToSpeechSendMessage(
                testText,
                TextToSpeechMessageType.RemoteTransmissionWithLocalPlayback);
            SetStatus($"Sent '{Truncate(testText, 32)}' via Vivox built-in TTS.");
        }
        catch (Exception ex)
        {
            SetStatus($"Vivox TTS failed: {ex.Message}");
            Debug.LogError($"[EchoTester] Vivox built-in TTS failed: {ex}");
        }
    }

    void StopInjection()
    {
        try
        {
            VivoxService.Instance.StopAudioInjection();
            SetStatus("StopAudioInjection called.");
        }
        catch (Exception ex)
        {
            SetStatus($"StopAudioInjection threw: {ex.Message}");
        }
    }

    async Task LeaveEchoChannelAsync()
    {
        _busy = true;
        try
        {
            SetStatus($"Leaving channel '{echoChannelName}'...");
            await VivoxService.Instance.LeaveChannelAsync(echoChannelName);
            SetStatus("Left echo channel.");
        }
        catch (Exception ex)
        {
            SetStatus($"Leave failed: {ex.Message}");
            Debug.LogError($"[EchoTester] Leave channel failed: {ex}");
        }
        finally { _busy = false; }
    }

    bool VivoxLoggedIn()
    {
        try { return VivoxService.Instance != null && VivoxService.Instance.IsLoggedIn; }
        catch { return false; }
    }

    string ActiveChannelSummary()
    {
        try
        {
            var channels = VivoxService.Instance?.ActiveChannels;
            if (channels == null || channels.Count == 0) return "(none)";
            return string.Join(", ", channels.Keys);
        }
        catch { return "(unavailable)"; }
    }

    static SupertonicLanguage ShiftLanguage(SupertonicLanguage current, int dir)
    {
        var values = (SupertonicLanguage[])Enum.GetValues(typeof(SupertonicLanguage));
        var idx = Array.IndexOf(values, current);
        if (idx < 0) idx = 0;
        idx = (idx + dir + values.Length) % values.Length;
        return values[idx];
    }

    static string Truncate(string s, int max)
    {
        return string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...");
    }

    void SetStatus(string s)
    {
        _status = s;
        Debug.Log($"[EchoTester] {s}");
    }

    static GUIStyle _richLabel;
    static GUIStyle RichLabel()
    {
        if (_richLabel == null)
        {
            _richLabel = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        }
        return _richLabel;
    }
}
