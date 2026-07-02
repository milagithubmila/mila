# שתילת מנוע ה-PRT3 בתוך דוגמת ה-Security של Crestron

מדריך זה מראה בדיוק אילו קבצים להוסיף ואילו עריכות לבצע בפרויקט הדוגמה של
Crestron (`SecuritySystem_Crestron_SampleDriverModel_IP`), כדי להפוך אותו
לדרייבר Paradox אמיתי שמדבר עם PRT3.

## החלטת תצורה: TCP/IP (מומלץ)
- **TCP/IP דרך ממיר Serial↔Ethernet** — זו התצורה שאנו בונים. גמיש (הלוח בכל מקום ברשת).
- **Serial ישיר** — עובד גם, אותה לוגיקה בדיוק, רק שהדרייבר מסוג Serial מקבל `IComPort`
  במקום `IPAddress/port`. מתאים רק אם ה-PRT3 צמוד פיזית ל-CP4-R עם COM פנוי.

מכאן והלאה — גרסת ה-**IP**.

## ארכיטקטורה
מנוע ה-PRT3 שלנו (`Prt3Engine`) פותח בעצמו את החיבור לממיר, סורק `RZ`/`RA`,
ומפיץ אירועי שינוי. שכבת ה-`SecuritySystemProtocol` של Crestron רק "מתרגמת"
את האירועים האלה לאובייקטי ה-Zone/Area של Crestron Home.

```
Crestron Home  ◄─events─  SecuritySystemProtocol  ◄─events─  Prt3Engine ──socket──► ממיר ──► PRT3
   (UI/אוטומציה)          (המתרגם ששותלים בו)                (הקוד הבדוק שלנו)
```

מיפוי מצבים:
| PRT3 | אצל Crestron |
|------|--------------|
| גלאי Open (תנועה) | `SecuritySystemZoneState.Faulted` פעיל |
| גלאי Closed | `Faulted` לא פעיל |
| מחיצה דרוכה | `SecuritySystemState.ArmedAway` |
| מחיצה מנוטרלת | `SecuritySystemState.Disarmed` |
| מחיצה באזעקה | `SecuritySystemAlarmType.Burglary` פעיל |

---

## שלב א' — הוסף לפרויקט את 3 הקבצים שלנו
מהתיקייה `driver/src/` הוסף לפרויקט הדוגמה (Right-click project → Add → Existing Item):
- `ParadoxProtocol.cs`
- `Prt3Engine.cs`
- `ParadoxTcpTransport.cs`

> הם ב-namespace `Mila.Paradox.Prt3` ואינם תלויים ב-SDK. הלוגיקה שלהם בדוקה
> ב-22 בדיקות. **הערת תאימות:** הם משתמשים ב-`System.Net.Sockets` וב-`System.Threading`.
> על CP4-R (4-Series) זה עובד. אם המהדר/הפלטפורמה מתלוננים — כתוב לי ואמיר את
> התעבורה ל-`Crestron.SimplSharp.CrestronSockets` (אותה לוגיקה).

---

## שלב ב' — ערוך את `SecuritySystemDriverIP.cs`
בסוף המתודה `Initialize(IPAddress ipAddress, int port)` (אחרי `InitializeKeypad();`),
הוסף שורה שמפעילה את המנוע עם ה-Host/Port ש-Crestron Home מעביר:

```csharp
    InitializeKeypad();

    // ↓↓↓ הוסף: הפעלת מנוע ה-PRT3 עם היעד שהוגדר ב-Crestron Home
    _securitySystemProtocol.StartEngine(ipAddress.ToString(), port);
```

בנוסף, ב-constructor הפעל את יכולת האזורים. שנה את `CapabilityList` כך שיכלול Zones:
```csharp
List<SecuritySystemCapabilities> CapabilityList = new List<SecuritySystemCapabilities>
{
    SecuritySystemCapabilities.DirectControl,
    SecuritySystemCapabilities.KeypadEmulation,
    SecuritySystemCapabilities.Zones          // ← הוסף כדי לאפשר גלאים
};
```
(בגרסת ה-IP זה כבר כך; ודא שהשורה קיימת.)

---

## שלב ג' — ערוך את `SecuritySystemProtocol.cs`
זה הקובץ המרכזי. שמור על כל אזור ה-Keypad ומחלקות ה-Args כפי שהם. בצע:

### ג.1 — הוסף using בראש הקובץ
```csharp
using Mila.Paradox.Prt3;
```

### ג.2 — הוסף שדות (ב-#region Fields)
```csharp
// ==== שילוב Paradox PRT3 ====
private Prt3Engine _engine;
private readonly Dictionary<int, SecuritySystemZone> _zoneMap = new Dictionary<int, SecuritySystemZone>();
private readonly Dictionary<int, SecuritySystemArea> _areaMap = new Dictionary<int, SecuritySystemArea>();

// >>> ערוך כאן: אילו גלאים ומחיצות לנטר, וקוד המשתמש לדריכה/נטרול <<<
private static readonly int[] MonitoredZones = { 1, 2, 3, 4, 5, 6, 7, 8 };
private static readonly int[] MonitoredAreas = { 1 };
private const string UserPin = "1234";
```

### ג.3 — הוסף את מתודת ההפעלה ואת יוצרי ה-Zone/Area (ב-#region Public Method)
```csharp
/// <summary>מופעל מהדרייבר עם Host/Port. יוצר Zones/Areas ומתחיל polling.</summary>
public void StartEngine(string host, int port)
{
    foreach (var a in MonitoredAreas) CreateArea(a);
    var firstArea = MonitoredAreas.Length > 0 ? MonitoredAreas[0] : 1;
    foreach (var z in MonitoredZones) CreateZone(z, firstArea);

    var cfg = new Prt3EngineConfig
    {
        Zones = new List<int>(MonitoredZones),
        Areas = new List<int>(MonitoredAreas),
        PollIntervalMs = 400
    };
    _engine = new Prt3Engine(new ParadoxTcpTransport(host, port), cfg) { Logger = LogMessage };
    _engine.ZoneChanged      += OnEngineZoneChanged;
    _engine.AreaChanged      += OnEngineAreaChanged;
    _engine.ConnectionChanged += (s, connected) => ConnectionChanged(connected);
    _engine.Start();
    LogMessage(string.Format("Paradox PRT3 engine started -> {0}:{1}", host, port));
}

private void CreateArea(int index)
{
    var states = new List<SecuritySystemState>
        { SecuritySystemState.ArmedAway, SecuritySystemState.ArmedStay, SecuritySystemState.Disarmed };
    var alarms = new List<SecuritySystemAlarmType>
        { SecuritySystemAlarmType.Burglary, SecuritySystemAlarmType.Fire, SecuritySystemAlarmType.Alarm };
    var cmds = new List<SecuritySystemAreaCommand>
    {
        new SecuritySystemAreaCommand(1, SecuritySystemCommandType.Disarm, true),
        new SecuritySystemAreaCommand(2, SecuritySystemCommandType.Away,   true),
        new SecuritySystemAreaCommand(3, SecuritySystemCommandType.Stay,   true)
    };
    var area = new SecuritySystemArea("Area " + index, index, states.AsReadOnly(),
        alarms.AsReadOnly(), cmds.AsReadOnly(), this);
    area.SecuritysystemAreaStateChangedEvent  += OnSecuritysystemAreaStateChangedEvent;
    area.SecuritysystemAlarmStateChangedEvent += OnSecuritysystemAlarmStateChangedEvent;
    _areas.Add(area);
    _areaMap[index] = area;
    var h = AreaListChanged;
    if (h != null) h(this, new ListChangedEventArgs<ISecuritySystemArea>(ListChangedAction.Added, null, area, _areas.Count - 1));
}

private void CreateZone(int index, int areaIndex)
{
    var zone = new SecuritySystemZone("Zone " + index, index, areaIndex);
    _zones.Add(zone);
    _zoneMap[index] = zone;
    var h = ZoneListChanged;
    if (h != null) h(this, new ListChangedEventArgs<ISecuritySystemZone>(ListChangedAction.Added, null, zone, _zones.Count - 1));
}

private void OnEngineZoneChanged(object sender, ZoneChangedEventArgs e)
{
    SecuritySystemZone zone;
    if (_zoneMap.TryGetValue(e.Zone, out zone))
        zone.SetActiveState(SecuritySystemZoneState.Faulted, e.IsMotion); // תנועה/פתוח = Faulted
}

private void OnEngineAreaChanged(object sender, AreaChangedEventArgs e)
{
    SecuritySystemArea area;
    if (!_areaMap.TryGetValue(e.Area, out area)) return;
    if (e.Arm == ArmState.Armed)         area.SetArmedMode(SecuritySystemCommandType.Away);
    else if (e.Arm == ArmState.Disarmed) area.SetArmedMode(SecuritySystemCommandType.Disarm);
    area.SetAlarmExternal(SecuritySystemAlarmType.Burglary, e.Alarm == AlarmState.InAlarm);
}
```

### ג.4 — החלף את גוף `DataHandler` (כבר לא צריך את נתוני הדמה)
```csharp
public override void DataHandler(string rx)
{
    switch (rx)
    {
        case "InitializationComplete": ConnectionChanged(true);  break;
        case "IsOffline":              ConnectionChanged(false); break;
    }
}
```
(אפשר למחוק את `CreateFakeSecruritySystemArea` / `CreateFakeSecuritySystemZone` — לא בשימוש.)

### ג.5 — החלף את `ExecuteSecurityCommands` (שליחת דריכה/נטרול אמיתית ל-PRT3)
```csharp
public SecuritySystemOperationalResult ExecuteSecurityCommands(int commandIndex, List<int> areaIndexes, string password)
{
    SecuritySystemAreaCommand cmd = null;
    foreach (var c in _securitySystem.GetAvailableAreaCommands())
        if (c.Index == commandIndex) cmd = c;

    var code = SecuritySystemOperationalResultCode.Success;
    if (cmd == null)
        code = SecuritySystemOperationalResultCode.InvalidIdParameters;
    else if (_engine != null)
    {
        var pin = string.IsNullOrEmpty(password) ? UserPin : password;
        foreach (var areaIndex in areaIndexes)
        {
            switch (cmd.CommandType)
            {
                case SecuritySystemCommandType.Disarm:    _engine.Disarm(areaIndex, pin); break;
                case SecuritySystemCommandType.Away:
                case SecuritySystemCommandType.ForceAway: _engine.Arm(areaIndex, pin, ArmMode.Regular); break;
                case SecuritySystemCommandType.Stay:
                case SecuritySystemCommandType.ForceStay: _engine.Arm(areaIndex, pin, ArmMode.Stay); break;
            }
        }
    }

    var result = new SecuritySystemOperationalResult(1)
    {
        CommandType     = cmd != null ? cmd.CommandType : SecuritySystemCommandType.Disarm,
        TargetComponentId = areaIndexes,
        Result          = code
    };
    var handler = SystemCommandResult;
    if (handler != null) handler(this, new SecuritySystemCommandResultEventArgs { CommandResult = result });
    return result;   // מצב הדריכה בפועל יתעדכן כשה-polling של RA יזהה את השינוי
}
```

### ג.6 — עצור את המנוע ב-`Dispose`
בתחילת `Dispose()` הוסף:
```csharp
if (_engine != null) _engine.Stop();
```

---

## שלב ד' — הוסף מתודה פומבית ל-`SecuritySystemZone.cs`
בתוך המחלקה (למשל אחרי `Poll()`), הוסף:
```csharp
/// <summary>נקרא מהמנוע: מסמן/מסיר מצב זון (Faulted = תנועה/פתוח).</summary>
public void SetActiveState(SecuritySystemZoneState state, bool active)
{
    UpdateActiveState(state, active);
}
```

---

## שלב ה' — הוסף שתי מתודות פומביות ל-`SecuritySystemArea.cs`
```csharp
/// <summary>נקרא מהמנוע: קובע מצב דריכה לפי סוג פקודה.</summary>
public void SetArmedMode(SecuritySystemCommandType commandType)
{
    UpdateArmingState(commandType);
}

/// <summary>נקרא מהמנוע: מסמן/מסיר אזעקה על המחיצה.</summary>
public void SetAlarmExternal(SecuritySystemAlarmType type, bool active)
{
    UpdateAlarmState(type, active);
}
```

---

## שלב ו' — הפוך את `SampleTransport.cs` לפסיבי
המנוע שלנו פותח את הסוקט האמיתי, לכן תעבורת ה-SDK חייבת להישאר פסיבית
(אחרת ייפתחו שני חיבורים לאותו ממיר). החלף את `Start()`:
```csharp
public override void Start()
{
    base.Start();
    var handler = DataHandler;
    if (handler != null) handler("InitializationComplete");
    // אין SendTransportData — הנתונים האמיתיים מגיעים ממנוע ה-PRT3.
}
```

---

## שלב ז' — בנייה
Build → Release. חבילת ManifestUtil תיצור את ה-`.pkg` תחת `bin\Release`.

## הגדרות שכדאי לשנות
- `MonitoredZones` / `MonitoredAreas` (שלב ג.2) — התאם למספרי הגלאים/מחיצות האמיתיים.
- `UserPin` — קוד המשתמש לדריכה/נטרול (אם משתמשים).
- ה-Host/Port מגיעים אוטומטית מהגדרת ההתקן ב-Crestron Home (מה-IP שהזנת).

## אם משהו לא מתקמפל
זה צפוי בגרסה ראשונה — שלח לי את הודעת השגיאה המדויקת ואתקן. הנקודה היחידה
שתלויה-פלטפורמה היא ה-`System.Net.Sockets` בתעבורה; אם צריך, אחליף ל-CrestronSockets.
