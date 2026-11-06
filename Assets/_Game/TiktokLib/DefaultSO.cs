using System.IO;
using Unity.Mathematics;
using UnityEngine;
public abstract class DefaultSO : ScriptableObject
{
    public string DefaultJsonFileName => string.IsNullOrEmpty(overrideJsonName) ? name : overrideJsonName;
    public string overrideJsonName;
    protected virtual void OnEnable()
    {
#if !UNITY_EDITOR
        LoadJson($"{Application.streamingAssetsPath}/{DefaultJsonFileName}");
#endif
    }
    [ContextMenu("Save")]
    public void Save()
    {
        SaveJson($"{Application.streamingAssetsPath}/{DefaultJsonFileName}");
    }
    [ContextMenu("Load")]
    public void Load()
    {
        LoadJson($"{Application.streamingAssetsPath}/{DefaultJsonFileName}");
    }
    private void SaveJson(string filePath = null)
    {
        Debug.Log(Application.persistentDataPath);
        string path = GetJsonPath(filePath);
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, SaveContent());
    }

    private bool LoadJson(string filePath = null)
    {
        string path = GetJsonPath(filePath);
        if (!File.Exists(path))
        {
            return false;
        }
        string json = File.ReadAllText(path);
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning($"Load data By {filePath} Failed! ");
            return false;
        }
        OnLoadContent(json);
        return true;
    }
    private string GetJsonPath(string filePath = null)
    {
        return string.IsNullOrEmpty(filePath) ? Path.Combine(Application.persistentDataPath, DefaultJsonFileName) : filePath;
    }

    protected abstract string SaveContent();
    protected abstract bool OnLoadContent(string json);
}