using UnityEngine;

// Generic PlayerPrefs-backed JSON persistence for one [Serializable] settings object. Replaces the
// per-settings Save/Load/Reset copies. Load() overwrites the live instance in place so references held by
// subsystems stay valid; Reset() clears the saved blob and copies fresh defaults into the live instance.
public sealed class JsonPref<T> where T : class
{
    readonly string key;
    public JsonPref(string key) { this.key = key; }

    public void Save(T value)
    {
        PlayerPrefs.SetString(key, JsonUtility.ToJson(value));
        PlayerPrefs.Save();
    }

    public void Load(T into)
    {
        var json = PlayerPrefs.GetString(key, "");
        if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, into);
    }

    public void Clear()
    {
        PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
    }

    public void Reset(T into, T defaults)   // clear saved blob + copy defaults into the live object
    {
        Clear();
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(defaults), into);
    }
}
