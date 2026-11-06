using UnityEngine;

    // Configuration holder only. Event-to-gameplay mappings are intentionally separate.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TikTokUdpReceiver))]
    public sealed class TikTokConnection : MonoBehaviour
    {
        public TiktokConfigSO Configuration;
        public TikTokUdpReceiver Receiver;
    }
