// Prt3Engine.cs
// =============
// מנוע ה-polling: סוקר גלאים ומחיצות מ-PRT3, משווה למצב הקודם, ומפיץ
// אירועי C# (.NET events) על כל שינוי. המנוע חף מתלות ב-SDK של Crestron,
// כך שניתן להשתמש בו גם בדרייבר CCD, גם ב-SIMPL# Pro, וגם לבדיקה.
//
// שכבת התעבורה (ITransport) מופשטת: ב-Crestron היא תעטוף TCPClient או
// ComPort (ראו ParadoxTransport.cs). הלוגיקה כאן זהה למימוש ה-Python
// שנבדק (gateway/paradox_prt3.py).

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Mila.Paradox.Prt3
{
    /// <summary>הפשטת תעבורה: בייט-בודד פנימה/החוצה. ממומש מעל TCP או טורי.</summary>
    public interface ITransport
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        void SendLine(string line);          // מוסיף CR בעצמו
        /// <summary>תו אחד, או null אם הגיע timeout ללא נתון.</summary>
        char? ReadByte(int timeoutMs);
    }

    public sealed class ZoneChangedEventArgs : EventArgs
    {
        public int Zone { get; set; }
        public ZoneState State { get; set; }
        public ZoneState Previous { get; set; }
        public bool IsMotion =>
            State == ZoneState.Open || State == ZoneState.Tamper || State == ZoneState.Fire;
    }

    public sealed class AreaChangedEventArgs : EventArgs
    {
        public int Area { get; set; }
        public ArmState Arm { get; set; }
        public AlarmState Alarm { get; set; }
    }

    public sealed class Prt3EngineConfig
    {
        public List<int> Zones { get; set; } = new List<int>();
        public List<int> Areas { get; set; } = new List<int>();
        public int PollIntervalMs { get; set; } = 400;
        public int LineTimeoutMs { get; set; } = 1000;
        public int ReconnectDelayMs { get; set; } = 3000;
    }

    public sealed class Prt3Engine : IDisposable
    {
        private readonly ITransport _transport;
        private readonly Prt3EngineConfig _cfg;
        private readonly Dictionary<int, ZoneState> _zoneState = new Dictionary<int, ZoneState>();
        private readonly Dictionary<int, AreaChangedEventArgs> _areaState = new Dictionary<int, AreaChangedEventArgs>();
        private readonly StringBuilder _rx = new StringBuilder();
        private readonly object _ioLock = new object();
        private Thread _thread;
        private volatile bool _running;

        public event EventHandler<ZoneChangedEventArgs> ZoneChanged;
        public event EventHandler<AreaChangedEventArgs> AreaChanged;
        public event EventHandler<bool> ConnectionChanged; // true=connected

        /// <summary>הוק לוג אופציונלי (בדרייבר Crestron: engine.Logger = LogMessage).</summary>
        public Action<string> Logger;

        public Prt3Engine(ITransport transport, Prt3EngineConfig cfg)
        {
            _transport = transport;
            _cfg = cfg;
        }

        public ZoneState GetZoneState(int zone)
        {
            ZoneState s;
            return _zoneState.TryGetValue(zone, out s) ? s : ZoneState.Unknown;
        }

        public bool IsAreaArmed(int area)
        {
            AreaChangedEventArgs a;
            return _areaState.TryGetValue(area, out a) && a.Arm == ArmState.Armed;
        }

        // ----- ניהול thread ----- //
        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Prt3Engine" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive) _thread.Join(2000);
            _transport.Close();
        }

        public void Dispose() => Stop();

        private void Loop()
        {
            EnsureOpen();
            while (_running)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    OnError(ex);
                    Reconnect();
                }
                Thread.Sleep(_cfg.PollIntervalMs);
            }
        }

        private void EnsureOpen()
        {
            try
            {
                if (!_transport.IsOpen) _transport.Open();
                RaiseConnection(true);
            }
            catch (Exception ex) { OnError(ex); }
        }

        private void Reconnect()
        {
            RaiseConnection(false);
            try { _transport.Close(); } catch { }
            while (_running)
            {
                try { _transport.Open(); RaiseConnection(true); return; }
                catch { Thread.Sleep(_cfg.ReconnectDelayMs); }
            }
        }

        // ----- סקירה ----- //
        public void PollOnce()
        {
            foreach (int zone in _cfg.Zones)
            {
                var m = Request(ParadoxProtocol.BuildZoneStatusRequest(zone));
                if (m != null && m.Kind == MessageKind.ZoneStatus) UpdateZone(m.Zone, m.ZoneState);
            }
            foreach (int area in _cfg.Areas)
            {
                var m = Request(ParadoxProtocol.BuildAreaStatusRequest(area));
                if (m != null && m.Kind == MessageKind.AreaStatus) UpdateArea(m.Area, m.ArmState, m.AlarmState);
            }
        }

        /// <summary>שולח פקודה וקורא תשובה רלוונטית אחת (מדלג על echo).</summary>
        public Prt3Message Request(string command)
        {
            lock (_ioLock)
            {
                _transport.SendLine(command);
                for (int i = 0; i < 5; i++)
                {
                    string line = ReadLine();
                    if (line == null) return null;
                    var parsed = ParadoxProtocol.Parse(line);
                    if (parsed.Kind == MessageKind.ZoneStatus || parsed.Kind == MessageKind.AreaStatus)
                        return parsed;
                }
                return null;
            }
        }

        private string ReadLine()
        {
            long deadline = Environment.TickCount + _cfg.LineTimeoutMs;
            while (Environment.TickCount < deadline)
            {
                char? c = _transport.ReadByte(_cfg.LineTimeoutMs);
                if (c == null) continue;
                if (c == '\r' || c == '\n')
                {
                    if (_rx.Length > 0)
                    {
                        string line = _rx.ToString();
                        _rx.Clear();
                        return line;
                    }
                    continue;
                }
                _rx.Append(c.Value);
            }
            return null;
        }

        private void UpdateZone(int zone, ZoneState state)
        {
            ZoneState prev;
            bool known = _zoneState.TryGetValue(zone, out prev);
            if (!known || prev != state)
            {
                _zoneState[zone] = state;
                var h = ZoneChanged;
                if (h != null)
                    h(this, new ZoneChangedEventArgs
                    {
                        Zone = zone, State = state, Previous = known ? prev : ZoneState.Unknown
                    });
            }
        }

        private void UpdateArea(int area, ArmState arm, AlarmState alarm)
        {
            AreaChangedEventArgs prev;
            bool known = _areaState.TryGetValue(area, out prev);
            if (!known || prev.Arm != arm || prev.Alarm != alarm)
            {
                var args = new AreaChangedEventArgs { Area = area, Arm = arm, Alarm = alarm };
                _areaState[area] = args;
                var h = AreaChanged;
                if (h != null) h(this, args);
            }
        }

        // ----- שליחת פקודות בקרה (דריכה/נטרול) ----- //
        public bool Arm(int area, string pin, ArmMode mode = ArmMode.Regular) =>
            SendControl(ParadoxProtocol.BuildArmCommand(area, pin, mode));

        public bool Disarm(int area, string pin) =>
            SendControl(ParadoxProtocol.BuildDisarmCommand(area, pin));

        private bool SendControl(string command)
        {
            lock (_ioLock)
            {
                _transport.SendLine(command);
                for (int i = 0; i < 5; i++)
                {
                    string line = ReadLine();
                    if (line == null) return false;
                    var parsed = ParadoxProtocol.Parse(line);
                    if (parsed.Kind == MessageKind.Ack) return parsed.Ok;
                }
                return false;
            }
        }

        private void RaiseConnection(bool connected)
        {
            var h = ConnectionChanged;
            if (h != null) h(this, connected);
        }

        private void OnError(Exception ex)
        {
            // בדרייבר אמיתי — חבר ל-Logger של ה-SDK. כאן מודפס בלבד.
            System.Diagnostics.Debug.WriteLine("Prt3Engine error: " + ex.Message);
        }
    }
}
