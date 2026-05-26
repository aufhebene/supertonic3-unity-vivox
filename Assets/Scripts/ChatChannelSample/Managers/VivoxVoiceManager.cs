using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Vivox;
using System;
using System.IO;
using System.Threading.Tasks;
using Supertonic.Unity;
#if AUTH_PACKAGE_PRESENT
using Unity.Services.Authentication;
#endif

public class VivoxVoiceManager : MonoBehaviour
{
    public const string LobbyChannelName = "lobbyChannel";

    // Check to see if we're about to be destroyed.
    static object m_Lock = new object();
    static VivoxVoiceManager m_Instance;

    //These variables should be set to the projects Vivox credentials if the authentication package is not being used
    //Credentials are available on the Vivox Developer Portal (developer.vivox.com) or the Unity Dashboard (dashboard.unity3d.com), depending on where the organization and project were made
    [SerializeField]
    string _key;
    [SerializeField]
    string _issuer;
    [SerializeField]
    string _domain;
    [SerializeField]
    string _server;

    public bool useSupertonic = false;

    [Tooltip("Target sample rate for the WAV file injected into Vivox. Must match Vivox's negotiated codec rate. Opus commonly uses 48000. Set to <= 0 to disable resampling and inject the TTS output as-is.")]
    [SerializeField]
    int vivoxTargetSampleRate = 48000;
    
    /// <summary>
    /// Access singleton instance through this propriety.
    /// </summary>
    public static VivoxVoiceManager Instance
    {
        get
        {
            lock (m_Lock)
            {
                if (m_Instance == null)
                {
                    // Search for existing instance.
                    m_Instance = (VivoxVoiceManager)FindObjectOfType(typeof(VivoxVoiceManager));

                    // Create new instance if one doesn't already exist.
                    if (m_Instance == null)
                    {
                        // Need to create a new GameObject to attach the singleton to.
                        var singletonObject = new GameObject();
                        m_Instance = singletonObject.AddComponent<VivoxVoiceManager>();
                        singletonObject.name = typeof(VivoxVoiceManager).ToString() + " (Singleton)";
                    }
                }
                // Make instance persistent even if its already in the scene
                DontDestroyOnLoad(m_Instance.gameObject);
                return m_Instance;
            }
        }
    }

    async void Awake()
    {
        if (m_Instance != this && m_Instance != null)
        {
            Debug.LogWarning(
                "Multiple VivoxVoiceManager detected in the scene. Only one VivoxVoiceManager can exist at a time. The duplicate VivoxVoiceManager will be destroyed.");
            Destroy(this);
        }
        var options = new InitializationOptions();
        if (CheckManualCredentials())
        {
            options.SetVivoxCredentials(_server, _domain, _issuer, _key);
        }

        SupertonicTtsManager.Instance.OnAudioDataGenerated += OnAudioDataGenerated;
        
        await UnityServices.InitializeAsync(options);
        await VivoxService.Instance.InitializeAsync();

    }

    private void OnDestroy()
    {
        if (SupertonicTtsManager.HasInstance)
        {
            SupertonicTtsManager.Instance.OnAudioDataGenerated -= OnAudioDataGenerated;
        }
    }

    void OnAudioDataGenerated(float[] audioData, int sampleRate)
    {
        if (!useSupertonic) return;

        var sourceDurationSec = audioData.Length / (float)sampleRate;
        var filePath = Path.Combine(Application.persistentDataPath, "SupertonicAudio.wav");

        // Resample to Vivox's expected rate if a target is configured.
        // Vivox docs require WAV sample rate to match the negotiated audio codec rate.
        var outputData = audioData;
        var outputRate = sampleRate;
        if (vivoxTargetSampleRate > 0 && vivoxTargetSampleRate != sampleRate)
        {
            try
            {
                outputData = ResampleLinear(audioData, sampleRate, vivoxTargetSampleRate);
                outputRate = vivoxTargetSampleRate;
                Debug.Log($"[Vivox-TTS] Resampled {sampleRate}Hz -> {outputRate}Hz " +
                          $"({audioData.Length} -> {outputData.Length} samples, {sourceDurationSec:F2}s preserved).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Vivox-TTS] Resampling failed: {ex.Message}. Falling back to source rate {sampleRate}Hz.");
                outputData = audioData;
                outputRate = sampleRate;
            }
        }

        try
        {
            SaveToWav(filePath, outputData, outputRate);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vivox-TTS] Failed to write WAV file: {ex.Message}");
            return;
        }

        var vivox = VivoxService.Instance;
        if (vivox == null)
        {
            Debug.LogError("[Vivox-TTS] VivoxService.Instance is null. Audio injection skipped.");
            return;
        }

        if (!vivox.IsLoggedIn)
        {
            Debug.LogError("[Vivox-TTS] Not logged in to Vivox. Audio injection skipped. " +
                           "Make sure VivoxService.Instance.LoginAsync(...) has completed before sending TTS.");
            return;
        }

        var channelCount = vivox.ActiveChannels?.Count ?? 0;
        if (channelCount == 0)
        {
            Debug.LogError("[Vivox-TTS] No active channels. Audio injection will not be heard. " +
                           "Join a channel via VivoxService.Instance.JoinGroupChannelAsync(...) first.");
            return;
        }

        var channelNames = vivox.ActiveChannels != null
            ? string.Join(", ", vivox.ActiveChannels.Keys)
            : "(unknown)";

        if (vivox.IsInputDeviceMuted)
        {
            Debug.LogWarning("[Vivox-TTS] Input device is muted. Audio injection may not transmit to remote participants.");
        }

        if (outputRate != 32000 && outputRate != 48000 && outputRate != 16000 && outputRate != 8000)
        {
            Debug.LogWarning($"[Vivox-TTS] WAV sample rate is {outputRate}Hz, which is unusual for Vivox. " +
                             "Vivox docs require the WAV's sample rate to match the negotiated audio codec rate " +
                             "(commonly 16000/32000/48000Hz). Audio may be silent or pitch-shifted for remote listeners.");
        }

        try
        {
            vivox.StartAudioInjection(filePath);
            Debug.Log($"[Vivox-TTS] Audio injection requested. " +
                      $"file={filePath}, samples={outputData.Length}, rate={outputRate}Hz, duration={sourceDurationSec:F2}s, " +
                      $"channels=[{channelNames}], inputMuted={vivox.IsInputDeviceMuted}. " +
                      $"Remote participants in these channels should now hear the synthesized audio.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vivox-TTS] StartAudioInjection threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static float[] ResampleLinear(float[] input, int sourceRate, int targetRate)
    {
        if (input == null || input.Length == 0)
            return new float[0];
        if (sourceRate == targetRate)
            return input;
        if (sourceRate <= 0 || targetRate <= 0)
            throw new ArgumentException($"Invalid sample rates: source={sourceRate}, target={targetRate}");

        long outputLength = (long)input.Length * targetRate / sourceRate;
        if (outputLength <= 0) outputLength = 1;
        var output = new float[outputLength];

        double ratio = (double)sourceRate / targetRate;
        int lastIndex = input.Length - 1;

        for (long i = 0; i < outputLength; i++)
        {
            double sourcePos = i * ratio;
            int floor = (int)sourcePos;
            if (floor >= lastIndex)
            {
                output[i] = input[lastIndex];
                continue;
            }
            double frac = sourcePos - floor;
            output[i] = (float)(input[floor] * (1.0 - frac) + input[floor + 1] * frac);
        }

        return output;
    }

    public async Task InitializeAsync(string playerName)
    {

#if AUTH_PACKAGE_PRESENT
        if (!CheckManualCredentials())
        {
            AuthenticationService.Instance.SwitchProfile(playerName);
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
#endif
    }

    bool CheckManualCredentials()
    {
        return !(string.IsNullOrEmpty(_issuer) && string.IsNullOrEmpty(_domain) && string.IsNullOrEmpty(_server));
    }
    
    public void TextToSpeechSendMessage(string message)
    {
        if (useSupertonic)
        {
            SupertonicTtsManager.Instance.Synthesize(message);
        }
        else
        {
            VivoxService.Instance.TextToSpeechSendMessage(message, TextToSpeechMessageType.RemoteTransmissionWithLocalPlayback);
        }
    }
    
    void SaveToWav(string filePath, float[] audioData, int sampleRate)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Create))
        using (var writer = new BinaryWriter(fileStream))
        {
            int sampleCount = audioData.Length;
            int channelCount = 1; // mono

            writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));
            writer.Write(36 + sampleCount * 2);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.UTF8.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channelCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channelCount * 2);
            writer.Write((short)(channelCount * 2));
            writer.Write((short)16);

            writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));
            writer.Write(sampleCount * 2);

            foreach (var sample in audioData)
            {
                short intSample = (short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
                writer.Write(intSample);
            }
        }

        Debug.Log($"[Vivox-TTS] WAV saved: {filePath} ({audioData.Length} samples @ {sampleRate}Hz)");
    }
}
