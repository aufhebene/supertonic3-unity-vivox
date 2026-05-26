using System.Collections.Generic;
using System.Linq;
using Supertonic.Unity;
using TMPro;
using UnityEngine;

public class VoiceStyleSelector : MonoBehaviour
{
    public TMP_Dropdown voiceStyleDropdown;

    void Start()
    {
        if (voiceStyleDropdown == null)
        {
            Debug.LogError("VoiceStyleSelector: voiceStyleDropdown is not assigned.");
            return;
        }

        var styles = SupertonicTtsManager.GetAvailableVoiceStyles();
        voiceStyleDropdown.ClearOptions();
        voiceStyleDropdown.AddOptions(styles);

        var current = SupertonicTtsManager.Instance.CurrentVoiceStyle;
        var index = styles.FindIndex(s => string.Equals(s, current, System.StringComparison.Ordinal));
        if (index < 0) index = Mathf.Max(0, styles.IndexOf("M1"));

        voiceStyleDropdown.value = index;
        voiceStyleDropdown.RefreshShownValue();
        SupertonicTtsManager.Instance.LoadVoiceStyle(styles[index]);

        voiceStyleDropdown.onValueChanged.AddListener(OnVoiceStyleChanged);
    }

    void OnVoiceStyleChanged(int index)
    {
        var name = voiceStyleDropdown.options[index].text;
        SupertonicTtsManager.Instance.LoadVoiceStyle(name);
        Debug.Log($"Selected voice style: {name}");
    }
}
