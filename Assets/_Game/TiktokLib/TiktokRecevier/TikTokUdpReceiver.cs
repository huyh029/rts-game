using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[RequireComponent(typeof(MainThreadDispatcher))]
public class TikTokUdpReceiver : MonoBehaviour
{
    public static TikTokUdpReceiver Instance { get; private set;  }
    [Header("UDP")]
    public int port = 7777;
    public bool logRaw = false;
    
    [Header("Performance")]
    [Tooltip("Timeout cho mỗi lần Receive (ms). Giảm = phản hồi nhanh hơn khi stop, tăng = ít CPU hơn")]
    [Range(10, 5000)]
    public int receiveTimeoutMs = 500;  // Giảm từ 1000ms → 100ms
    
    [Header("Auto Start")]
    [Tooltip("Tự động start receiver khi OnEnable. Tắt nếu muốn GameManager control")]
    public bool autoStart = false;

    public static Action<TikEvent> OnEvent;

    UdpClient _udp;
    Thread _thread;
    volatile bool _running;
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(Instance.gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
    void OnEnable()
    {
        if (autoStart)
            StartReceiver();
    }
    
    void OnDisable() => StopReceiver();
    private void OnDestroy()
    {
        Instance = null;
        StopReceiver();
    }
    public void StartReceiver()
    {
        if (_running) return;

        try
        {
            _udp = new UdpClient(port);
            _udp.Client.ReceiveTimeout = receiveTimeoutMs;  // Dùng timeout có thể config

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();

            Debug.Log($"[UDP] Listening on 0.0.0.0:{port} (timeout: {receiveTimeoutMs}ms)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDP] StartReceiver error: {e}");
            StopReceiver();
        }
    }

    public void StopReceiver()
    {
        _running = false;

        try { _udp?.Close(); } catch { }
        _udp = null;

        try { _thread?.Join(200); } catch { }
        _thread = null;
    }

    void Loop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
                string json = Encoding.UTF8.GetString(data);

                // if (logRaw) Debug.Log($"[UDP RAW] {json}");

                // ✅ parse bằng JsonUtility (Unity built-in)
                TikEvent ev = JsonUtility.FromJson<TikEvent>(json);
                if (ev == null) continue;

                ev.rawJson = json; // debug
                MainThreadDispatcher.Run(() =>
                {
                    OnEvent?.Invoke(ev);
                });
            }
            catch (SocketException)
            {
                // timeout
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UDP] Loop error: {e.Message}");
            }
        }
    }

    // Public method để test - trigger event manually
    public void TriggerTestEvent(TikEvent tikEvent)
    {
        OnEvent?.Invoke(tikEvent);
    }
}