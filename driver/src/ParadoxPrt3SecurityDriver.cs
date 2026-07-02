// ParadoxPrt3SecurityDriver.cs
// ============================
// שכבת הקישור (adapter) בין מנוע ה-PRT3 (Prt3Engine — חף מ-SDK) לבין
// מסגרת ה-Crestron Certified Drivers (CCD).
//
// ┌───────────────────────────────────────────────────────────────────────┐
// │ ⭐ הדרך הבטוחה לבנות לבד (ראה docs/05 + פרק 5 במדריך ה-HTML):            │
// │                                                                         │
// │   אל תתחיל מהקובץ הזה מאפס. במקום זאת:                                    │
// │   1. פתח את דוגמת ה-Security הרשמית מה-SDK                               │
// │      (Samples\...\SecuritySystemDriverIP.cs) — היא מתקמפלת מושלם.        │
// │   2. הוסף לפרויקט שלה את 3 הקבצים שלי חסרי-התלות:                          │
// │        ParadoxProtocol.cs · Prt3Engine.cs · ParadoxTcpTransport.cs      │
// │   3. בתוך ה-protocol class של הדוגמה, צור Prt3Engine, האזן לאירועים      │
// │      שלו, וקרא למתודות ה-Area/Zone של הדוגמה (ראה "מפת השתילה" למטה).     │
// │                                                                         │
// │   כך אתה בטוח שהחתימות תואמות בדיוק לגרסת ה-SDK שלך — ולא צריך לנחש.       │
// └───────────────────────────────────────────────────────────────────────┘
//
// הקובץ הזה הוא *הפניה* שמראה את חיווט המנוע. שמות ה-namespace והממשקים כאן
// אומתו מול תיעוד Crestron (SDK v20+):
//   namespace  : Crestron.RAD.DeviceTypes.SecuritySystem
//   ממשקים     : ISecuritySystem, ISecuritySystemArea
//   בסיס משותף : Crestron.RAD.Common.BasicDriver.ABaseDriverProtocol
// אך החתימות המדויקות (שמות מתודות/enum) נלקחות מקובץ הדוגמה שלך.
//
// תיעוד:
//   Security System Drivers:
//   https://sdkcon78221.crestron.com/sdk/Crestron_Certified_Drivers_SDK/Content/Topics/Create-Security-System.htm
//   Implementation Diagrams:
//   .../Device-Types/Security-System/Security-System-Diagrams.htm

#if CRESTRON_RAD   // הגדר סמל זה רק כשמקמפלים מול ה-SDK (מוגדר ב-csproj)

using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.SecuritySystem;

namespace Mila.Paradox.Prt3
{
    // ═══════════════════════════════════════════════════════════════════════
    // מפת השתילה (Integration Map) — מה לחבר למה בתוך דוגמת ה-SDK:
    //
    //   האירוע/הפעולה שלי            →   מה לעשות בדוגמת ה-SDK
    //   ─────────────────────────       ─────────────────────────────────────
    //   engine.ZoneChanged (Open)    →   עדכן מצב ה-Zone המתאים ב-Area
    //                                    (Open/Closed) — property/מתודה של
    //                                    ISecuritySystemArea בדוגמה.
    //   engine.ZoneChanged (Closed)  →   עדכן ל-Closed.
    //   engine.AreaChanged (Armed)   →   עדכן ArmingState של ה-Area ל-"Armed".
    //   engine.AreaChanged (Disarmed)→   עדכן ל-"Disarmed".
    //   engine.AreaChanged (InAlarm) →   הפעל את דיווח האזעקה של ה-Area.
    //   פקודת Arm מ-Crestron         →   קרא engine.Arm(area, pin, mode).
    //   פקודת Disarm מ-Crestron      →   קרא engine.Disarm(area, pin).
    //   engine.ConnectionChanged     →   דווח Online/Offline (מנגנון ה-SDK).
    //
    // הערכים המדויקים של ArmingState/AlarmType נמצאים ב-enum של ה-SDK בדוגמה.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// דוגמת שלד לחיבור המנוע. יש להתאים את שם מחלקת הבסיס ואת קריאות ה-API
    /// לפי SecuritySystemDriverIP.cs שבדוגמת ה-SDK.
    /// </summary>
    public class ParadoxPrt3SecurityProtocol : ABaseDriverProtocol
    {
        private Prt3Engine _engine;
        private string _userPin = "";

        // ABaseDriverProtocol דורש את התעבורה וה-id. אמת את החתימה בדוגמה.
        public ParadoxPrt3SecurityProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }

        /// <summary>נקרא לאחר טעינת ההגדרות (Host/Port/Zones/Partitions/PIN).</summary>
        public void StartEngine(Prt3EngineConfig config, string userPin)
        {
            _userPin = userPin;

            // תעבורת TCP עצמאית אל הממיר Serial<->Ethernet.
            // (חלופה: לעטוף את ConnectionTransport של ה-SDK תחת ITransport.)
            ITransport t = new ParadoxTcpTransport(config_Host(config), config_Port(config));
            _engine = new Prt3Engine(t, config);

            _engine.ZoneChanged += (s, e) => ReportZone(e.Zone, e.State);
            _engine.AreaChanged += (s, e) => ReportArea(e.Area, e.Arm, e.Alarm);
            _engine.ConnectionChanged += (s, connected) => ReportOnline(connected);
            _engine.Start();
        }

        // ── חבר את שלוש המתודות הבאות ל-API של הדוגמה (SecuritySystemArea) ──

        private void ReportZone(int zone, ZoneState state)
        {
            // TODO(מהדוגמה): עדכן את הזון 'zone' ל-Open אם state==Open אחרת Closed.
            // הדוגמה מראה בדיוק איזה property/מתודה של ה-Area/Zone לקרוא ואיך
            // להעלות את אירוע השינוי ל-Crestron. (ל-Auto-Off של תאורת חדר די
            // בכך שהזון מדווח Open→Closed.)
        }

        private void ReportArea(int area, ArmState arm, AlarmState alarm)
        {
            // TODO(מהדוגמה): עדכן ArmingState של ה-Area (Armed/Disarmed),
            // ואם alarm==InAlarm — הפעל את דיווח האזעקה של ה-Area.
        }

        private void ReportOnline(bool connected)
        {
            // TODO(מהדוגמה): דווח על מצב חיבור ההתקן (Online/Offline).
        }

        // ── פקודות בקרה שמגיעות מ-Crestron Home ──
        // בדוגמה יש מתודות Arm/Disarm שאתה דורס. מתוכן קרא:
        public void ArmArea(int area)    => _engine?.Arm(area, _userPin, ArmMode.Regular);
        public void DisarmArea(int area, string code)
            => _engine?.Disarm(area, string.IsNullOrEmpty(code) ? _userPin : code);

        // עוזרים לקריאת ההגדרות — החלף בקריאת ההגדרות האמיתית של הדוגמה.
        private static string config_Host(Prt3EngineConfig c) => "192.168.1.50";
        private static int    config_Port(Prt3EngineConfig c) => ParadoxTcpTransportDefaults.Port;

        protected override void ConnectionChangedEvent(bool connection) { }
    }

    internal static class ParadoxTcpTransportDefaults { public const int Port = 10001; }
}

#endif  // CRESTRON_RAD
