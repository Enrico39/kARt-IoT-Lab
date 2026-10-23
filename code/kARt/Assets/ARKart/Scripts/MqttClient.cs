using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ARKart
{
    /// <summary>
    /// Client MQTT 3.1.1 minimo, senza dipendenze: CONNECT con Last Will, PUBLISH (QoS 0/1,
    /// retained), PINGREQ e DISCONNECT. Basta per pubblicare stato e metriche del Quest
    /// (project.md §3.5) e funziona con IL2CPP senza problemi di stripping.
    /// Non è thread-safe: va usato da un solo thread (quello di KartMqtt).
    /// </summary>
    public class MqttClient : IDisposable
    {
        TcpClient tcp;
        NetworkStream stream;
        ushort packetId;

        public bool Connected => tcp != null && tcp.Connected;

        public void Connect(string host, int port, string clientId, ushort keepAliveSec,
            string willTopic, string willPayload, bool willRetain)
        {
            tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 3000, SendTimeout = 3000 };
            if (!tcp.ConnectAsync(host, port).Wait(3000)) throw new IOException("timeout di connessione al broker");
            stream = tcp.GetStream();

            // Variable header: nome protocollo, livello 4 (= 3.1.1), flag, keep alive
            var body = new List<byte>();
            AddString(body, "MQTT");
            body.Add(4);
            byte flags = 0x02;                                   // clean session
            if (willTopic != null)
            {
                flags |= 0x04 | (1 << 3);                        // will flag, will QoS 1
                if (willRetain) flags |= 0x20;
            }
            body.Add(flags);
            body.Add((byte)(keepAliveSec >> 8));
            body.Add((byte)keepAliveSec);
            // Payload: client id, poi topic e messaggio del Last Will
            AddString(body, clientId);
            if (willTopic != null)
            {
                AddString(body, willTopic);
                AddString(body, willPayload);
            }
            Send(0x10, body);

            // CONNACK: 0x20 0x02 <session present> <return code>
            ReadPacket(out byte type, out byte[] payload);
            if (type >> 4 != 2 || payload.Length < 2) throw new IOException("risposta inattesa dal broker");
            if (payload[1] != 0) throw new IOException($"connessione rifiutata dal broker (codice {payload[1]})");
        }

        public void Publish(string topic, string payload, bool retain = false, int qos = 0)
        {
            var body = new List<byte>();
            AddString(body, topic);
            if (qos > 0)
            {
                packetId = (ushort)(packetId % 65535 + 1);
                body.Add((byte)(packetId >> 8));
                body.Add((byte)packetId);
            }
            body.AddRange(Encoding.UTF8.GetBytes(payload));
            Send((byte)(0x30 | (qos << 1) | (retain ? 1 : 0)), body);
        }

        public void Ping() => Send(0xC0, null);

        /// <summary>Legge e scarta le risposte del broker (PUBACK, PINGRESP), senza bloccare.</summary>
        public void DrainIncoming()
        {
            while (tcp.Available > 0)
                ReadPacket(out _, out _);
        }

        public void Disconnect()
        {
            try { if (Connected) Send(0xE0, null); }
            catch (Exception) { /* chiusura comunque */ }
            Dispose();
        }

        public void Dispose()
        {
            stream?.Dispose();
            tcp?.Close();
            stream = null;
            tcp = null;
        }

        // ------------------------------------------------------------ Codifica

        static void AddString(List<byte> buffer, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            buffer.Add((byte)(b.Length >> 8));
            buffer.Add((byte)b.Length);
            buffer.AddRange(b);
        }

        void Send(byte header, List<byte> body)
        {
            int length = body?.Count ?? 0;
            var packet = new List<byte>(length + 5) { header };
            // Remaining length: 7 bit per byte, il bit alto dice se ne segue un altro
            do
            {
                byte digit = (byte)(length % 128);
                length /= 128;
                if (length > 0) digit |= 0x80;
                packet.Add(digit);
            } while (length > 0);
            if (body != null) packet.AddRange(body);
            byte[] data = packet.ToArray();
            stream.Write(data, 0, data.Length);
        }

        void ReadPacket(out byte header, out byte[] payload)
        {
            header = ReadByte();
            int length = 0, multiplier = 1;
            byte digit;
            do
            {
                digit = ReadByte();
                length += (digit & 0x7F) * multiplier;
                multiplier *= 128;
            } while ((digit & 0x80) != 0 && multiplier <= 128 * 128 * 128);

            payload = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int n = stream.Read(payload, offset, length - offset);
                if (n <= 0) throw new IOException("connessione al broker chiusa");
                offset += n;
            }
        }

        byte ReadByte()
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("connessione al broker chiusa");
            return (byte)b;
        }
    }
}
