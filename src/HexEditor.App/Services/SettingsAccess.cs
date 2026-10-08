using HexEditor.Core.Settings;

namespace HexEditor.App.Services;

/// <summary>設定 (SettingsStore) を Platform の <see cref="ISettingsAccess"/> として渡す。</summary>
public sealed class SettingsAccess(SettingsStore store) : ISettingsAccess
{
    public string GetString(string key, string defaultValue) => store.GetString(key, defaultValue);

    public bool GetBool(string key, bool defaultValue) => store.GetBool(key, defaultValue);

    public void SetString(string key, string value, string defaultValue) => store.SetString(key, value, defaultValue);
}
