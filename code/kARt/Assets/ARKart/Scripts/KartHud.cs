using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ARKart
{
    /// <summary>
    /// HUD del pilota (project.md §6.2). La gerarchia grafica la crea il menu
    /// "kARt/1. Crea interfaccia" (ARKartSetup); qui si aggiornano solo i valori.
    /// Fuori dal Play mostra valori di esempio, per vedere il design direttamente nella scena.
    /// </summary>
    [ExecuteAlways]
    public class KartHud : MonoBehaviour
    {
        public KartLink link;
        public MjpegStream video;
        public KartMqtt mqtt;

        [Tooltip("Fuori dal Play mostra valori di esempio")]
        public bool previewInEditor = true;

        [Header("Soglie sensore posteriore (cm)")]
        public int warnCm = 30;
        public int stopCm = 15;
        public int sensorRangeCm = 60;

        [Header("Riferimenti (impostati dal menu kARt)")]
        public TMP_Text connectionText;
        public Image connectionDot;
        public Image connectionPill;
        public TMP_Text stateText;
        public Image stateAccent;
        public GameObject emergencyBanner;
        public Image throttleFill;
        public TMP_Text throttleValue;
        public Image steerFill;
        public TMP_Text steerValue;
        public Image panFill;
        public TMP_Text panValue;
        public TMP_Text rearValue;
        public Image[] rearSegments;
        public TMP_Text footerText;
        public TMP_Text videoStatus;
        public Image videoStatusPill;

        // --- Palette ---
        public static readonly Color Green = Hex(0x34C77B);
        public static readonly Color Yellow = Hex(0xF5B83D);
        public static readonly Color Red = Hex(0xEF4E4E);
        public static readonly Color Blue = Hex(0x4C9AFF);
        public static readonly Color Orange = Hex(0xFF8A3D);
        public static readonly Color TextPrimary = Hex(0xE8ECF2);
        public static readonly Color TextSecondary = Hex(0x8B95A5);
        public static readonly Color Unlit = new Color(1f, 1f, 1f, 0.08f);

        public static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        struct Values
        {
            public bool connected, emergency;
            public string state;
            public int throttle, steer, pan, rearCm, sent, received;
            public float rttMs;
            public bool videoLive, mqttConnected;
            public float videoFps;
        }

        static Values Preview => new Values
        {
            connected = true, emergency = false, state = "WARN",
            throttle = -140, steer = 70, pan = -20, rearCm = 24,
            rttMs = 42f, sent = 1834, received = 1829,
            videoLive = true, videoFps = 30f, mqttConnected = true,
        };

        void Update()
        {
            bool preview = !Application.isPlaying ? previewInEditor : link == null;
            if (!preview && link == null) return;

            var v = preview ? Preview : new Values
            {
                connected = link.Connected, emergency = link.EmergencyStop, state = link.KartState,
                throttle = link.Throttle, steer = link.Steer, pan = link.Pan, rearCm = link.RearDistanceCm,
                rttMs = link.RttMs, sent = link.PacketsSent, received = link.TelemetryReceived,
                videoLive = video != null && video.Live, videoFps = video != null ? video.Fps : 0f,
                mqttConnected = mqtt != null && mqtt.Connected,
            };
            Render(v, link != null ? link.maxPan : 45);
        }

        void Render(Values v, int maxPan)
        {
            // Connessione
            Color connColor = v.connected ? Green : Red;
            if (connectionText) connectionText.text = v.connected ? "CONNESSO" : "NON RAGGIUNGIBILE";
            if (connectionDot) connectionDot.color = connColor;
            if (connectionPill) connectionPill.color = new Color(connColor.r, connColor.g, connColor.b, 0.15f);

            // Stato del kart
            string state = v.connected ? v.state : "—";
            Color stateColor = state switch
            {
                "OK" => Green,
                "WARN" => Yellow,
                "STOP" => Red,
                "FAILSAFE" => Red,
                _ => TextSecondary,
            };
            if (stateText) { stateText.text = state; stateText.color = stateColor; }
            if (stateAccent) stateAccent.color = stateColor;
            if (emergencyBanner && emergencyBanner.activeSelf != v.emergency) emergencyBanner.SetActive(v.emergency);

            // Barre centrate sullo zero
            SetBar(throttleFill, v.throttle / 255f, v.throttle >= 0 ? Green : Orange);
            if (throttleValue) throttleValue.text = $"{Mathf.RoundToInt(v.throttle / 2.55f)}%";
            SetBar(steerFill, v.steer / 255f, Blue);
            if (steerValue) steerValue.text = v.steer == 0 ? "dritto" : $"{(v.steer < 0 ? "sx" : "dx")} {Mathf.Abs(Mathf.RoundToInt(v.steer / 2.55f))}%";
            SetBar(panFill, maxPan > 0 ? (float)v.pan / maxPan : 0f, TextPrimary);
            if (panValue) panValue.text = v.pan == 0 ? "centro" : $"{(v.pan < 0 ? "sx" : "dx")} {Mathf.Abs(v.pan)}°";

            // Sensore di parcheggio posteriore
            if (rearValue)
            {
                rearValue.text = v.rearCm < 0 ? "libero" : $"{v.rearCm} cm";
                rearValue.color = v.rearCm < 0 ? TextSecondary : v.rearCm < stopCm ? Red : v.rearCm < warnCm ? Yellow : TextPrimary;
            }
            if (rearSegments != null && rearSegments.Length > 0)
            {
                int n = rearSegments.Length;
                int lit = 0;
                if (v.rearCm >= 0 && v.rearCm < sensorRangeCm)
                    lit = Mathf.Clamp(Mathf.CeilToInt((sensorRangeCm - v.rearCm) / (float)(sensorRangeCm - stopCm) * n), 1, n);
                for (int i = 0; i < n; i++)
                {
                    if (!rearSegments[i]) continue;
                    float pos = (i + 0.5f) / n;
                    Color on = pos < 0.4f ? Green : pos < 0.75f ? Yellow : Red;
                    rearSegments[i].color = i < lit ? on : Unlit;
                }
            }

            // Riga in basso
            if (footerText)
            {
                string rtt = v.rttMs >= 0 ? $"{v.rttMs:0} ms" : "—";
                string mqttState = v.mqttConnected ? "<color=#34C77B>collegato</color>" : "<color=#EF4E4E>non collegato</color>";
                footerText.text = $"RTT  <color=#E8ECF2>{rtt}</color>     ·     Pacchetti  <color=#E8ECF2>{v.sent}</color> inviati / <color=#E8ECF2>{v.received}</color> ricevuti     ·     MQTT  {mqttState}";
            }

            // Pannello video
            if (videoStatus) videoStatus.text = v.videoLive ? $"● LIVE  {v.videoFps:0} FPS" : "● OFFLINE";
            if (videoStatusPill) videoStatusPill.color = v.videoLive ? new Color(Green.r, Green.g, Green.b, 0.35f) : new Color(1f, 1f, 1f, 0.12f);
        }

        static void SetBar(Image fill, float value, Color color)
        {
            if (!fill) return;
            value = Mathf.Clamp(value, -1f, 1f);
            var rt = fill.rectTransform;
            float end = 0.5f + value * 0.5f;
            rt.anchorMin = new Vector2(Mathf.Min(0.5f, end), 0f);
            rt.anchorMax = new Vector2(Mathf.Max(0.5f, end), 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            fill.color = color;
        }
    }
}
