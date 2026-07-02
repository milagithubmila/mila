// ParadoxProtocol.cs
// ==================
// ליבת פרוטוקול ה-ASCII של Paradox APR-PRT3 (Printer Module) במצב Home Automation.
//
// קובץ זה עצמאי לחלוטין ואינו תלוי ב-SDK של Crestron — הוא אחראי אך ורק על:
//   * בניית מחרוזות פקודה (RZ / RA / AA / AD)
//   * פענוח שורות תשובה מ-PRT3 למבני נתונים
//
// אותה לוגיקה בדיוק ממומשת ונבדקת ב-Python (gateway/paradox_prt3.py +
// test_paradox_prt3.py). אם תרצה לאמת התנהגות — הרץ את בדיקות ה-Python.
//
// הפניות פרוטוקול: Paradox "APR3-PRT3 Printer Module — ASCII Protocol
// Programming Instructions". כל הודעה מסתיימת ב-CR (0x0D). תקשורת 8N1.

using System;
using System.Globalization;

namespace Mila.Paradox.Prt3
{
    public enum ZoneState
    {
        Closed,   // C — סגור / ללא תנועה / תקין
        Open,     // O — פתוח / תנועה זוהתה
        Tamper,   // T — חבלה
        Fire,     // F — לולאת אש
        Unknown
    }

    public enum ArmState
    {
        Disarmed, // D
        Armed,    // A
        Unknown
    }

    public enum AlarmState
    {
        Ok,       // O
        InAlarm,  // A
        Unknown
    }

    public enum ArmMode
    {
        Regular,  // A
        Force,    // F
        Stay,     // S
        Instant   // I
    }

    public enum MessageKind { ZoneStatus, AreaStatus, Ack, Unknown }

    /// <summary>תוצאת פענוח של שורה בודדת מ-PRT3.</summary>
    public sealed class Prt3Message
    {
        public MessageKind Kind { get; private set; }

        // ZoneStatus
        public int Zone { get; private set; }
        public ZoneState ZoneState { get; private set; }

        // AreaStatus
        public int Area { get; private set; }
        public ArmState ArmState { get; private set; }
        public AlarmState AlarmState { get; private set; }

        // Ack
        public string Command { get; private set; }
        public bool Ok { get; private set; }

        public string Raw { get; private set; }

        public static Prt3Message ZoneMsg(int zone, ZoneState s) =>
            new Prt3Message { Kind = MessageKind.ZoneStatus, Zone = zone, ZoneState = s };

        public static Prt3Message AreaMsg(int area, ArmState arm, AlarmState alarm) =>
            new Prt3Message { Kind = MessageKind.AreaStatus, Area = area, ArmState = arm, AlarmState = alarm };

        public static Prt3Message AckMsg(string cmd, bool ok) =>
            new Prt3Message { Kind = MessageKind.Ack, Command = cmd, Ok = ok };

        public static Prt3Message UnknownMsg(string raw) =>
            new Prt3Message { Kind = MessageKind.Unknown, Raw = raw };

        /// <summary>True כשהגלאי מדווח פתיחה/תנועה — הטריגר להדלקת אור.</summary>
        public bool IsMotion =>
            Kind == MessageKind.ZoneStatus &&
            (ZoneState == ZoneState.Open || ZoneState == ZoneState.Tamper || ZoneState == ZoneState.Fire);
    }

    public static class ParadoxProtocol
    {
        public const char Cr = '\r';
        public const string AckOk = "&ok";
        public const string AckFail = "&fail";

        // ----- בניית פקודות (ללא CR — שכבת התעבורה מוסיפה אותו) ----- //

        public static string BuildZoneStatusRequest(int zone)
        {
            if (zone < 1 || zone > 999) throw new ArgumentOutOfRangeException(nameof(zone));
            return "RZ" + zone.ToString("000", CultureInfo.InvariantCulture);
        }

        public static string BuildAreaStatusRequest(int area)
        {
            if (area < 1 || area > 999) throw new ArgumentOutOfRangeException(nameof(area));
            return "RA" + area.ToString("000", CultureInfo.InvariantCulture);
        }

        public static string BuildArmCommand(int area, string pin, ArmMode mode = ArmMode.Regular)
        {
            char m;
            switch (mode)
            {
                case ArmMode.Regular: m = 'A'; break;
                case ArmMode.Force:   m = 'F'; break;
                case ArmMode.Stay:    m = 'S'; break;
                case ArmMode.Instant: m = 'I'; break;
                default: throw new ArgumentException("invalid mode", nameof(mode));
            }
            return "AA" + area.ToString("000", CultureInfo.InvariantCulture) + m + pin;
        }

        public static string BuildDisarmCommand(int area, string pin) =>
            "AD" + area.ToString("000", CultureInfo.InvariantCulture) + pin;

        // ----- פענוח ----- //

        private static ZoneState ZoneStateFromChar(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'C': return ZoneState.Closed;
                case 'O': return ZoneState.Open;
                case 'T': return ZoneState.Tamper;
                case 'F': return ZoneState.Fire;
                default:  return ZoneState.Unknown;
            }
        }

        private static ArmState ArmStateFromChar(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'D': return ArmState.Disarmed;
                case 'A': return ArmState.Armed;
                default:  return ArmState.Unknown;
            }
        }

        private static AlarmState AlarmStateFromChar(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'O': return AlarmState.Ok;
                case 'A': return AlarmState.InAlarm;
                default:  return AlarmState.Unknown;
            }
        }

        private static bool ThreeDigits(string s, int start) =>
            s.Length >= start + 3 &&
            char.IsDigit(s[start]) && char.IsDigit(s[start + 1]) && char.IsDigit(s[start + 2]);

        /// <summary>מפענח שורת תשובה בודדת (ללא ה-CR).</summary>
        public static Prt3Message Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Prt3Message.UnknownMsg(raw);
            string msg = raw.Trim();
            if (msg.Length == 0) return Prt3Message.UnknownMsg(raw);

            string lower = msg.ToLowerInvariant();
            string upper = msg.ToUpperInvariant();
            bool hasAck = lower.Contains(AckOk) || lower.Contains(AckFail);

            // אישור echo טהור (לא RZ/RA נושאי-מידע)
            if (hasAck && !upper.StartsWith("RZ") && !upper.StartsWith("RA"))
            {
                string cmd = msg.Length >= 5 ? msg.Substring(0, 5) : msg;
                return Prt3Message.AckMsg(cmd, lower.Contains(AckOk));
            }

            // תשובת סטטוס גלאי: RZnnn<s>
            if (upper.StartsWith("RZ") && ThreeDigits(msg, 2) && msg.Length >= 6)
            {
                if (msg.IndexOf('&') >= 0)
                    return Prt3Message.AckMsg(msg.Substring(0, 5), lower.Contains(AckOk));
                int zone = int.Parse(msg.Substring(2, 3), CultureInfo.InvariantCulture);
                return Prt3Message.ZoneMsg(zone, ZoneStateFromChar(msg[5]));
            }

            // תשובת סטטוס מחיצה: RAnnn<arm>....<alarm>
            if (upper.StartsWith("RA") && ThreeDigits(msg, 2) && msg.Length >= 6)
            {
                if (msg.IndexOf('&') >= 0)
                    return Prt3Message.AckMsg(msg.Substring(0, 5), lower.Contains(AckOk));
                int area = int.Parse(msg.Substring(2, 3), CultureInfo.InvariantCulture);
                ArmState arm = ArmStateFromChar(msg[5]);
                // תו האזעקה ב-offset 10 (RA + 3 ספרות + arm + 4 תווים + alarm)
                AlarmState alarm = msg.Length > 10 ? AlarmStateFromChar(msg[10]) : AlarmState.Unknown;
                return Prt3Message.AreaMsg(area, arm, alarm);
            }

            return Prt3Message.UnknownMsg(msg);
        }
    }
}
