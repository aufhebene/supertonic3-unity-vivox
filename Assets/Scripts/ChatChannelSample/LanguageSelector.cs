using System;
using System.Linq;
using Supertonic.Unity;
using TMPro;
using UnityEngine;

public class LanguageSelector : MonoBehaviour
{
    public TMP_Dropdown languageDropdown;

    void Start()
    {
        if (languageDropdown == null)
        {
            Debug.LogError("LanguageSelector: languageDropdown is not assigned.");
            return;
        }

        var languageNames = Enum.GetNames(typeof(SupertonicLanguage)).ToList();
        languageDropdown.ClearOptions();
        languageDropdown.AddOptions(languageNames);

        var current = SupertonicTtsManager.Instance.CurrentLanguage;
        languageDropdown.value = (int)current;
        languageDropdown.RefreshShownValue();

        languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
    }

    void OnLanguageChanged(int index)
    {
        var values = Enum.GetValues(typeof(SupertonicLanguage));
        var clamped = Mathf.Clamp(index, 0, values.Length - 1);
        var lang = (SupertonicLanguage)values.GetValue(clamped);
        SupertonicTtsManager.Instance.SetLanguage(lang);
        Debug.Log($"Selected language: {lang}");
    }
}
