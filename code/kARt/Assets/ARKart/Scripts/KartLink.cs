using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ARKart
{
    /// <summary>
    /// Collegamento Quest ↔ kart (project.md §3).
    /// Invia C,&lt;seq&gt;,&lt;throttle&gt;,&lt;steer&gt;,&lt;pan&gt; a 30 Hz via UDP alla Pi e riceve
    /// la telemetria T,&lt;seq&gt;,&lt;dist_cm&gt;,&lt;stato&gt;, da cui calcola l'RTT.
    ///
    /// Comandi (project.md §4.3):
    ///   grilletto destro = acceleratore, grilletto sinistro = retromarcia,
    ///   stick sinistro X = sterzo, stick destro X = camera, click stick destro = camera al centro,
    ///   B = stop di emergenza (premi di nuovo per sbloccare).
    /// Nell'editor funziona anche da tastiera: W/S, A/D, Q/E, C, spazio.
    /// </summary>
    public class KartLink : MonoBehaviour
    {
        [Header("Rete")]
        [Tooltip("IP della Pi. Su Android i nomi .local non sono affidabili: meglio l'indirizzo numerico.")]
        public string piAddress = "192.168.1.70";
        public int commandPort = 5005;
        public int telemetryPort = 5006;
        public float sendRateHz = 30f;

        [Header("Guida")]
        [Range(0f, 0.5f)] public float deadzone = 0.1f;
        [Tooltip("Curva dell'acceleratore: >1 dà più precisione a bassa velocità")]
        [Range(1f, 3f)] public float throttleExpo = 1.6f;
        public int maxPan = 45;
        public float panSpeedDegPerSec = 90f;

        // --- Stato letto da HUD e pannello statistiche ---
        public int Throttle { get; private set; }
        public int Steer { get; private set; }
        public int Pan { get; private set; }
        public int RearDistanceCm { get; private set; } = -1;
        public string KartState { get; private set; } = "—";
        public float RttMs { get; private set; } = -1f;
        public bool EmergencyStop { get; private set; }
        public int PacketsSent { get; private set; }
        public int TelemetryReceived { get; private set; }
        public bool Connected => Clock() - lastTelemetryTime < 1.0;

        InputAction throttleAction, reverseAction, steerAction, panAction, panCenterAction, stopAction;

        UdpClient udp;
        IPEndPoint piEndPoint;
        Thread rxThread;
        volatile bool running;
        readonly ConcurrentQueue<(string line, double time)> rxQueue = new ConcurrentQueue<(string, double)>();
        readonly Dictionary<ushort, double> sentTimes = new Dictionary<ushort, double>();

        static readonly Stopwatch clock = Stopwatch.StartNew();
        static double Clock() => clock.Elapsed.TotalSeconds;   // usabile anche dal thread di ricezione

        ushort seq;
        double nextSendTime;
        float panValue;
        double lastTelemetryTime = -10;

        void OnEnable()
        {
            CreateInputActions();
            OpenSocket();
        }

        void OnDisable()
        {
            SendStop();
            CloseSocket();
            foreach (var a in new[] { throttleAction, reverseAction, steerAction, panAction, panCenterAction, stopAction })
                a?.Dispose();
        }

        // ------------------------------------------------------------ Input

        void CreateInputActions()
        {
            throttleAction = new InputAction("Throttle", InputActionType.Value, "<XRController>{RightHand}/{Trigger}");
            throttleAction.AddBinding("<Keyboard>/w");

            reverseAction = new InputAction("Reverse", InputActionType.Value, "<XRController>{LeftHand}/{Trigger}");
            reverseAction.AddBinding("<Keyboard>/s");

            steerAction = new InputAction("Steer", InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}/x");
            steerAction.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");

            panAction = new InputAction("Pan", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}/x");
            panAction.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/q").With("Positive", "<Keyboard>/e");

            panCenterAction = new InputAction("PanCenter", InputActionType.Button, "<XRController>{RightHand}/{Primary2DAxisClick}");
            panCenterAction.AddBinding("<Keyboard>/c");

            stopAction = new InputAction("EmergencyStop", InputActionType.Button, "<XRController>{RightHand}/{SecondaryButton}");
            stopAction.AddBinding("<Keyboard>/space");

            foreach (var a in new[] { throttleAction, reverseAction, steerAction, panAction, panCenterAction, stopAction })
                a.Enable();
        }

        float ApplyDeadzone(float v)
        {
            float abs = Mathf.Abs(v);
            if (abs < deadzone) return 0f;
            return Mathf.Sign(v) * (abs - deadzone) / (1f - deadzone);
        }

        void ReadInputs(float dt)
        {
            if (stopAction.WasPressedThisFrame()) EmergencyStop = !EmergencyStop;

            float forward = Mathf.Pow(ApplyDeadzone(throttleAction.ReadValue<float>()), throttleExpo);
            float backward = Mathf.Pow(ApplyDeadzone(reverseAction.ReadValue<float>()), throttleExpo);
            float steer = ApplyDeadzone(steerAction.ReadValue<float>());

            Throttle = EmergencyStop ? 0 : Mathf.RoundToInt((forward - backward) * 255f);
            Steer = EmergencyStop ? 0 : Mathf.RoundToInt(steer * 255f);

            if (panCenterAction.WasPressedThisFrame()) panValue = 0f;
            panValue += ApplyDeadzone(panAction.ReadValue<float>()) * panSpeedDegPerSec * dt;
            panValue = Mathf.Clamp(panValue, -maxPan, maxPan);
            Pan = Mathf.RoundToInt(panValue);
        }

        // ------------------------------------------------------------ Rete

        void OpenSocket()
        {
            try
            {
                piEndPoint = new IPEndPoint(ResolveAddress(piAddress), commandPort);
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, telemetryPort));
                running = true;
                rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "KartLink RX" };
                rxThread.Start();
                UnityEngine.Debug.Log($"[KartLink] Pilota verso {piEndPoint}, telemetria su :{telemetryPort}");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[KartLink] Impossibile aprire la connessione: {e.Message}");
            }
        }

        static IPAddress ResolveAddress(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            foreach (var a in Dns.GetHostAddresses(host))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            throw new Exception($"Indirizzo non trovato: {host}");
        }

        void CloseSocket()
        {
            running = false;
            udp?.Close();            // sblocca la Receive del thread
            rxThread?.Join(200);
            udp = null;
        }

        void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] data = udp.Receive(ref any);
                    double t = Clock();
                    foreach (var line in Encoding.ASCII.GetString(data).Split('\n'))
                        if (line.Length > 0) rxQueue.Enqueue((line.Trim(), t));
                }
                catch (SocketException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        void Send(string line)
        {
            if (udp == null) return;
            byte[] data = Encoding.ASCII.GetBytes(line + "\n");
            try { udp.Send(data, data.Length, piEndPoint); }
            catch (SocketException) { /* rete giù: il kart andrà in FAILSAFE da solo */ }
        }

        void SendCommand()
        {
            seq++;
            Send($"C,{seq},{Throttle},{Steer},{Pan}");
            PacketsSent++;
            sentTimes[seq] = Clock();
            sentTimes.Remove((ushort)(seq - 300));
        }

        void SendStop()
        {
            seq++;
            Send($"C,{seq},0,0,0");
        }

        void ProcessTelemetry()
        {
            while (rxQueue.TryDequeue(out var item))
            {
                // T,<seq>,<dist_cm>,<stato>
                var parts = item.line.Split(',');
                if (parts.Length != 4 || parts[0] != "T") continue;
                if (!ushort.TryParse(parts[1], out var echoSeq) || !int.TryParse(parts[2], out var dist)) continue;

                RearDistanceCm = dist;
                KartState = parts[3];
                lastTelemetryTime = item.time;
                TelemetryReceived++;
                if (sentTimes.TryGetValue(echoSeq, out var sentAt))
                    RttMs = (float)((item.time - sentAt) * 1000.0);
            }
        }

        // ------------------------------------------------------------ Ciclo

        void Update()
        {
            ReadInputs(Time.unscaledDeltaTime);
            ProcessTelemetry();

            double now = Clock();
            if (now >= nextSendTime)
            {
                // niente raffiche: se il frame è arrivato in ritardo si invia un solo comando
                nextSendTime = Math.Max(nextSendTime + 1.0 / sendRateHz, now);
                SendCommand();
            }
        }

        void OnApplicationPause(bool paused)
        {
            // visore tolto o app in pausa: si ferma il kart subito, senza aspettare il watchdog
            if (paused) SendStop();
        }
    }
}
