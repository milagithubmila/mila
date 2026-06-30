// ParadoxPrt3SecurityDriver.cs
// ============================
// שכבת הקישור (adapter) בין מנוע ה-PRT3 (Prt3Engine — חף מ-SDK) לבין
// מסגרת ה-Crestron Certified Drivers (CCD). זהו הקובץ היחיד שתלוי ב-SDK
// של Crestron.
//
// ⚠️ הערה חשובה על תלות-גרסה:
//   חתימות המחלקות והמתודות של ה-CCD SDK משתנות בין גרסאות (V1 מול V2,
//   ובין מהדורות מינוריות). הקובץ הזה כתוב כתבנית עובדת לפי המבנה המתועד,
//   אך *חובה* לאמת את שמות מחלקות הבסיס/המתודות מול גרסת ה-SDK המותקנת
//   אצלך, ולהוסיף את ההפניות (References) ל-Crestron.RAD.*.dll.
//   ראו מדריך הבנייה ב-docs/05-build-crestron-driver.md.
//
//   תיעוד רלוונטי:
//   - Drivers SDK:            https://sdkcon78221.crestron.com/
//   - Security Device Type:   .../Driver-SDK-V1/Create-a-Driver/Device-Types/Security/
//   - Platform Device Type:   .../Driver-SDK-V1/Create-a-Driver/Device-Types/Platform/
//   - Crestron Home – Drivers: https://docs.crestron.com/ -> CP4-R -> Third-Party Drivers
//
// ----------------------------------------------------------------------------
// אדריכלות החשיפה ל-Crestron Home (מומלץ — ראו docs/01 ו-07):
//
//   * כל מחיצה (Partition) של Paradox נחשפת כ-Security Partition עם מצבי
//     Armed / Disarmed / In-Alarm. כך תקבל אריח אבטחה אמיתי ב-Crestron Home.
//
//   * כל גלאי (Zone) נחשף כ-Security Zone (Open/Closed) — וזה הטריגר
//     שעליו בונים את אוטומציית "תנועה -> אור -> טיימר" במנוע האוטומציה
//     של Crestron Home (Auto-Off של החדר).
//
// אם תרצה ניצול מלא של ה-Auto-Off המובנה של תאורת חדר ב-Crestron Home,
// אפשר לחשוף את גלאי התנועה גם כ-Occupancy Sensor (Platform/Sensor device
// type). שתי הגישות מתוארות ב-docs/07.
// ----------------------------------------------------------------------------

#if CRESTRON_RAD   // הגדר סמל זה רק כשמקמפלים מול ה-SDK של Crestron

using System;
using System.Collections.Generic;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Transports;
using Crestron.RAD.DeviceTypes.Security;   // ASecurity / ASecurityProtocol

namespace Mila.Paradox.Prt3
{
    /// <summary>
    /// פרוטוקול האבטחה של הדרייבר. CCD יוצר מופע, מזרים אליו את התעבורה,
    /// ומצפה שנודיע על שינויי מצב דרך ה-callbacks/properties של מחלקת הבסיס.
    /// </summary>
    public class ParadoxPrt3SecurityProtocol : ASecurityProtocol
    {
        private Prt3Engine _engine;
        private Prt3EngineConfig _config;
        private string _userPin = "";   // נטען מהגדרות הדרייבר

        public ParadoxPrt3SecurityProtocol(ISerialTransport transport, byte id)
            : base(transport, id)
        {
        }

        // נקרא ע"י ה-SDK לאחר שהתעבורה והגדרות הדרייבר נטענו.
        // ConnectionTransport כאן הוא ה-ITransport של ה-SDK; אנו עוטפים אותו.
        public void Initialize(Prt3EngineConfig config, string userPin)
        {
            _config = config;
            _userPin = userPin;

            // עטיפת תעבורת ה-SDK תחת ה-ITransport שלנו, או שימוש בתעבורה
            // עצמאית (ParadoxTcpTransport). ראו docs/05 לשתי האפשרויות.
            ITransport t = new SdkTransportAdapter(ConnectionTransport);
            _engine = new Prt3Engine(t, _config);
            _engine.ZoneChanged += OnZoneChanged;
            _engine.AreaChanged += OnAreaChanged;
            _engine.ConnectionChanged += (s, connected) => { /* TODO: דווח Online/Offline ל-SDK */ };
            _engine.Start();
        }

        private void OnZoneChanged(object sender, ZoneChangedEventArgs e)
        {
            // ⚠️ אמת מול ה-SDK שלך את שם ה-API לעדכון מצב זון.
            // לרוב קיים אובייקט/feedback לכל זון; כאן מדגימים את הרעיון:
            //   var zoneState = e.State == ZoneState.Open
            //       ? eSecurityZoneState.Open : eSecurityZoneState.Closed;
            //   SecurityZoneStateChange(e.Zone, zoneState);
            //   (או עדכון Property של אובייקט הזון המתאים)
        }

        private void OnAreaChanged(object sender, AreaChangedEventArgs e)
        {
            // עדכון מצב מחיצה (דרוך/מנוטרל/אזעקה) כלפי ה-SDK.
            //   if (e.Alarm == AlarmState.InAlarm)  PartitionAlarm(e.Area);
            //   else if (e.Arm == ArmState.Armed)   PartitionArmed(e.Area);
            //   else                                PartitionDisarmed(e.Area);
        }

        // ----- פקודות בקרה שמגיעות מ-Crestron Home ----- //

        public override void Arm(uint partition)        // חתימה לדוגמה — אמת מול ה-SDK
        {
            _engine?.Arm((int)partition, _userPin, ArmMode.Regular);
        }

        public override void Disarm(uint partition, string code)
        {
            _engine?.Disarm((int)partition, string.IsNullOrEmpty(code) ? _userPin : code);
        }

        public override void Dispose()
        {
            _engine?.Stop();
            base.Dispose();
        }
    }

    /// <summary>
    /// מתאם בין תעבורת ה-SDK (ISerialTransport / ITransport של Crestron)
    /// לבין ה-ITransport הפנימי של המנוע. אם בוחרים בתעבורת TCP עצמאית
    /// (ParadoxTcpTransport) — אין צורך במתאם הזה.
    /// </summary>
    internal sealed class SdkTransportAdapter : ITransport
    {
        private readonly object _sdkTransport;   // הטיפוס האמיתי תלוי-SDK
        public SdkTransportAdapter(object sdkTransport) { _sdkTransport = sdkTransport; }

        public bool IsOpen { get; private set; }
        public void Open()  { /* TODO: _sdkTransport.Start();  IsOpen = true; */ }
        public void Close() { /* TODO: _sdkTransport.Stop();   IsOpen = false; */ }
        public void SendLine(string line) { /* TODO: _sdkTransport.SendMethod(line + CR) */ }
        public char? ReadByte(int timeoutMs)
        {
            // ב-SDK התקבולת היא לרוב event-driven (DataHandler). במצב כזה
            // עדיף לחבר את ה-DataHandler ישירות ל-parser במקום polling של בייטים.
            // ראו docs/05 לשתי הארכיטקטורות.
            return null;
        }
    }
}

#endif  // CRESTRON_RAD
