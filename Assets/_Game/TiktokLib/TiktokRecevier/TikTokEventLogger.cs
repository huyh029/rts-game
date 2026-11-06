using UnityEngine;

[DisallowMultipleComponent]
public sealed class TikTokEventLogger : MonoBehaviour
{
    public bool LogEvents = true;
    private void OnEnable() => TikTokUdpReceiver.OnEvent += Log;
    private void OnDisable() => TikTokUdpReceiver.OnEvent -= Log;
    private void Log(TikEvent message)
    {
        if (!LogEvents || message == null) return;
        var payload = string.IsNullOrEmpty(message.rawJson) ? JsonUtility.ToJson(message, true) : message.rawJson;
        Debug.Log("[TikTok received: " + message.type + "]\n" + payload, this);
    }
}
