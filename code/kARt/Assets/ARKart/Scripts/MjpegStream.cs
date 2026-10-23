using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace ARKart
{
    /// <summary>
    /// Ricezione del video FPV, project.md §3.3: MJPEG su HTTP dalla Pi
    /// (arkart_camera.py, http://&lt;pi&gt;:8080/stream.mjpg).
    ///
    /// Un thread legge il flusso multipart e tiene solo l'ultimo JPEG arrivato; il main thread
    /// lo decodifica con Texture2D.LoadImage. Se il Quest è più lento della camera si saltano
    /// fotogrammi invece di accumulare ritardo. Se la connessione cade si riconnette da solo.
    /// </summary>
    public class MjpegStream : MonoBehaviour
    {
        [Tooltip("L'indirizzo della Pi è quello di KartLink")]
        public KartLink link;
        public int port = 8080;
        public string path = "/stream.mjpg";

        [Header("Riferimenti (impostati dal menu kARt)")]
        public RawImage target;
        public AspectRatioFitter fitter;
        [Tooltip("Scritta mostrata finché non arriva il primo fotogramma")]
        public GameObject placeholder;

        // --- Stato letto da HUD e pannello statistiche ---
        public bool Live => Clock() - lastFrameTime < 1.0;
        public float Fps { get; private set; }
        public int FramesShown { get; private set; }
        public int FramesDropped { get; private set; }

        static readonly Stopwatch clock = Stopwatch.StartNew();
        static double Clock() => clock.Elapsed.TotalSeconds;

        Texture2D texture;
        Thread rxThread;
        volatile bool running;
        TcpClient tcp;

        readonly object frameLock = new object();
        byte[] pendingFrame;          // ultimo JPEG ricevuto, non ancora mostrato

        double lastFrameTime = -10;
        double fpsWindowStart;
        int fpsWindowFrames;

        void OnEnable()
        {
            if (link == null) link = GetComponent<KartLink>();
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Video FPV" };
            running = true;
            rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MjpegStream RX" };
            rxThread.Start();
        }

        void OnDisable()
        {
            running = false;
            tcp?.Close();             // sblocca la lettura del thread
            rxThread?.Join(200);
            if (texture != null) Destroy(texture);
        }

        // ------------------------------------------------------------ Rete (thread)

        void ReceiveLoop()
        {
            string host = link != null ? link.piAddress : "192.168.1.70";
            while (running)
            {
                try
                {
                    var ip = KartLink.ResolveAddress(host);
                    tcp = new TcpClient { ReceiveTimeout = 3000, NoDelay = true };
                    if (!tcp.ConnectAsync(ip, port).Wait(3000)) throw new IOException("timeout di connessione");
                    UnityEngine.Debug.Log($"[MjpegStream] Collegato a http://{ip}:{port}{path}");

                    var stream = new BufferedStream(tcp.GetStream(), 64 * 1024);
                    byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                    ReadParts(stream);
                }
                catch (Exception e)
                {
                    if (running) UnityEngine.Debug.LogWarning($"[MjpegStream] Stream non disponibile ({e.Message}), riprovo");
                }
                finally
                {
                    tcp?.Close();
                }
                // pausa prima di riprovare, interrotta subito se il componente viene spento
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        // Legge le intestazioni riga per riga: alla riga vuota dopo un Content-Length segue il JPEG.
        // Vale anche per l'intestazione della risposta HTTP, che non ha Content-Length.
        void ReadParts(Stream stream)
        {
            int length = -1;
            while (running)
            {
                string line = ReadLine(stream);
                if (line == null) throw new IOException("connessione chiusa dalla Pi");

                if (line.Length == 0)
                {
                    if (length <= 0) continue;
                    var jpeg = new byte[length];
                    ReadExactly(stream, jpeg);
                    lock (frameLock)
                    {
                        if (pendingFrame != null) FramesDropped++;
                        pendingFrame = jpeg;
                    }
                    length = -1;
                }
                else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(15).Trim(), out length);
                }
            }
        }

        readonly StringBuilder lineBuilder = new StringBuilder(128);

        string ReadLine(Stream stream)
        {
            lineBuilder.Clear();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                if (b == '\n') break;
                if (b != '\r') lineBuilder.Append((char)b);
                if (lineBuilder.Length > 1024) throw new IOException("intestazione troppo lunga");
            }
            return lineBuilder.ToString();
        }

        static void ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.Read(buffer, offset, buffer.Length - offset);
                if (n <= 0) throw new IOException("connessione chiusa a metà fotogramma");
                offset += n;
            }
        }

        // ------------------------------------------------------------ Ciclo

        void Update()
        {
            byte[] jpeg;
            lock (frameLock)
            {
                jpeg = pendingFrame;
                pendingFrame = null;
            }

            double now = Clock();
            if (jpeg != null && texture.LoadImage(jpeg, false))
            {
                lastFrameTime = now;
                FramesShown++;
                fpsWindowFrames++;
                if (target)
                {
                    target.texture = texture;
                    target.color = Color.white;
                }
                if (fitter) fitter.aspectRatio = (float)texture.width / texture.height;
            }

            if (placeholder && placeholder.activeSelf == Live) placeholder.SetActive(!Live);
            if (target && !Live && target.texture != null) target.color = new Color(0.55f, 0.55f, 0.55f);  // fotogramma vecchio: si scurisce

            // FPS mostrati, su finestre da mezzo secondo
            if (now - fpsWindowStart >= 0.5)
            {
                Fps = (float)(fpsWindowFrames / (now - fpsWindowStart));
                fpsWindowStart = now;
                fpsWindowFrames = 0;
            }
        }
    }
}
