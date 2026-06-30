// ParadoxTcpTransport.cs
// ======================
// מימוש ITransport מעל TCP — לחיבור ל-PRT3 דרך ממיר Serial<->Ethernet
// (Lantronix XPort/UDS, USR-TCP232, Moxa NPort וכו'), או ישירות אם משתמשים
// בגשר טורי-IP. בקרי 4-Series (כולל CP4-R) תומכים ב-System.Net.Sockets.
//
// להערה: ה-CCD SDK מספק גם שכבת תעבורה משלו (ISerialTransport /
// ITcpTransport). אם מעדיפים להשתמש בה — אפשר לעטוף אותה תחת ITransport
// במקום מחלקה זו. ראו docs/05.

using System;
using System.Net.Sockets;
using System.Text;

namespace Mila.Paradox.Prt3
{
    public sealed class ParadoxTcpTransport : ITransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private TcpClient _client;
        private NetworkStream _stream;

        public ParadoxTcpTransport(string host, int port = 10001, int connectTimeoutMs = 5000)
        {
            _host = host;
            _port = port;
            _connectTimeoutMs = connectTimeoutMs;
        }

        public bool IsOpen => _client != null && _client.Connected;

        public void Open()
        {
            Close();
            _client = new TcpClient();
            var ar = _client.BeginConnect(_host, _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(_connectTimeoutMs))
            {
                _client.Close();
                throw new TimeoutException($"PRT3 connect timeout {_host}:{_port}");
            }
            _client.EndConnect(ar);
            _client.NoDelay = true;
            _stream = _client.GetStream();
        }

        public void Close()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void SendLine(string line)
        {
            if (_stream == null) throw new InvalidOperationException("transport not open");
            byte[] bytes = Encoding.ASCII.GetBytes(line + ParadoxProtocol.Cr);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        public char? ReadByte(int timeoutMs)
        {
            if (_stream == null) throw new InvalidOperationException("transport not open");
            _client.ReceiveTimeout = timeoutMs;
            try
            {
                int b = _stream.ReadByte();   // -1 בסגירה
                if (b < 0) throw new System.IO.IOException("peer closed");
                return (char)b;
            }
            catch (System.IO.IOException ioex)
            {
                // timeout נורמלי מתבטא כ-SocketException פנימי עם code 10060
                if (ioex.InnerException is SocketException se &&
                    (se.SocketErrorCode == SocketError.TimedOut ||
                     se.SocketErrorCode == SocketError.WouldBlock))
                    return null;
                throw;
            }
        }
    }
}
