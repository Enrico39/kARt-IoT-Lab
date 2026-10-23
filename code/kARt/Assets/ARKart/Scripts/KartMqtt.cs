using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace ARKart
{
    /// <summary>
    /// Il Quest come nodo IoT (project.md §3.5): pubblica sul broker della Pi
    ///   arkart/quest/status   "online" / "offline", retained, con Last Will "offline"
    ///   arkart/quest/metrics  {"rtt_ms":38,"video_fps":27,"lost_pkts":3,"battery":81} a 1 Hz
    /// così la dashboard Node-RED vede anche il visore.
    ///
    /// Gira su un thread suo: se il broker non risponde la guida non ne risente,
    /// e la connessione viene ritentata ogni 2 s.
    /// </summary>
    public class KartMqtt : MonoBehaviour
    {
        [Tooltip("Il broker è sulla Pi: l'indirizzo è quello di KartLink")]
        public KartLink link;
        public MjpegStream video;
        public int port = 1883;
        public float metricsPeriodSec = 1f;

        const string TopicStatus = "arkart/quest/status";
        const string TopicMetrics = "arkart/quest/metrics";
        const ushort KeepAliveSec = 10;

        // --- Stato letto dall'HUD ---
        public bool Connected => connected;

        volatile bool connected;
        volatile bool running;
        volatile bool paused;
        string pendingMetrics;             // JSON preparato dal main thread, inviato dal thread MQTT (via Interlocked)
        Thread thread;
        string host;
        float nextMetricsTime;

        void OnEnable()
        {
            if (link == null) link = GetComponent<KartLink>();
            if (video == null) video = GetComponent<MjpegStream>();
            host = link != null ? link.piAddress : "192.168.1.70";
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "KartMqtt" };
            thread.Start();
        }

        void OnDisable()
        {
            running = false;           // il thread pubblica "offline" e si scollega
            thread?.Join(1000);
        }

        // visore tolto o app in pausa: il Quest risulta offline finché non si riprende
        void OnApplicationPause(bool isPaused) => paused = isPaused;

        void Update()
        {
            if (Time.unscaledTime < nextMetricsTime) return;
            nextMetricsTime = Time.unscaledTime + metricsPeriodSec;
            Interlocked.Exchange(ref pendingMetrics, BuildMetrics());
        }

        string BuildMetrics()
        {
            var c = CultureInfo.InvariantCulture;
            string rtt = link != null && link.RttMs >= 0 ? Mathf.RoundToInt(link.RttMs).ToString(c) : "null";
            string fps = video != null && video.Live ? Mathf.RoundToInt(video.Fps).ToString(c) : "0";
            string lost = link != null ? link.LostPackets.ToString(c) : "0";
            float level = SystemInfo.batteryLevel;   // -1 se non disponibile (editor)
            string battery = level >= 0 ? Mathf.RoundToInt(level * 100f).ToString(c) : "null";
            return $"{{\"rtt_ms\":{rtt},\"video_fps\":{fps},\"lost_pkts\":{lost},\"battery\":{battery}}}";
        }

        // ------------------------------------------------------------ Thread MQTT

        void Loop()
        {
            while (running)
            {
                if (paused) { Thread.Sleep(200); continue; }
                var client = new MqttClient();
                try
                {
                    client.Connect(host, port, "arkart-quest", KeepAliveSec, TopicStatus, "offline", willRetain: true);
                    client.Publish(TopicStatus, "online", retain: true, qos: 1);
                    connected = true;
                    Debug.Log($"[KartMqtt] Collegato al broker {host}:{port}");

                    DateTime lastSend = DateTime.UtcNow;
                    while (running && !paused)
                    {
                        string metrics = Interlocked.Exchange(ref pendingMetrics, null);
                        if (metrics != null)
                        {
                            client.Publish(TopicMetrics, metrics);
                            lastSend = DateTime.UtcNow;
                        }
                        else if ((DateTime.UtcNow - lastSend).TotalSeconds > KeepAliveSec / 2)
                        {
                            client.Ping();   // nessun dato da inviare: si tiene viva la connessione
                            lastSend = DateTime.UtcNow;
                        }
                        client.DrainIncoming();
                        Thread.Sleep(50);
                    }

                    // uscita ordinata: con DISCONNECT il Last Will non parte, quindi "offline" va pubblicato qui
                    client.Publish(TopicStatus, "offline", retain: true, qos: 1);
                    client.Disconnect();
                }
                catch (Exception e)
                {
                    if (running) Debug.LogWarning($"[KartMqtt] Broker non raggiungibile ({e.Message}), riprovo");
                    client.Dispose();
                }
                connected = false;
                for (int i = 0; i < 20 && running && !paused; i++) Thread.Sleep(100);
            }
        }
    }
}
