# 05 — בניית הדרייבר ל-Crestron Home (קובץ `.pkg`)

שלב זה הופך את קוד ה-C# שב-`driver/` לקובץ **`.pkg`** שניתן לטעון ל-Crestron Home.
דורש סביבת פיתוח של Crestron.

## דרישות מקדימות
1. **חשבון מפתחים** ב-Crestron (Developer account) עם גישה ל-Drivers SDK.
2. **Crestron Drivers SDK** — מ-https://sdkcon78221.crestron.com/
   (כולל ה-DLLים: `Crestron.RAD.Common`, `Crestron.RAD.DeviceTypes.Security`,
   ועוד, וכן את **ManifestUtil.exe**).
3. **Visual Studio 2019+** עם .NET / netstandard2.0.

## מבנה הקוד (תזכורת)
| קובץ | תלות SDK | הערה |
|------|:--------:|------|
| `src/ParadoxProtocol.cs` | ❌ | פענוח/בניית פקודות — מקמפל לבד |
| `src/Prt3Engine.cs` | ❌ | polling + אירועי שינוי — מקמפל לבד |
| `src/ParadoxTcpTransport.cs` | ❌ | TCP (System.Net.Sockets) |
| `src/ParadoxPrt3SecurityDriver.cs` | ✅ | עטוף ב-`#if CRESTRON_RAD` |

## שלב 1 — לאמת את הליבה (ללא SDK)
מומלץ קודם לוודא שהליבה תקינה. ההתנהגות מאומתת ב-Python:
```bash
cd gateway && python3 -m unittest test_paradox_prt3 -v
```
מחלקת `ParadoxProtocol` ב-C# ממומשת לפי אותה התנהגות בדיוק.

## שלב 2 — לחבר את ה-References ל-SDK
ב-`Mila_SecuritySystem_Paradox_PRT3_IP.csproj` עדכן את ה-`HintPath` למיקום
ה-DLL של ה-SDK אצלך (או הגדר `CrestronSdkPath`):
```xml
<Reference Include="Crestron.RAD.Common">
  <HintPath>C:\CrestronSDK\Crestron.RAD.Common.dll</HintPath>
</Reference>
<Reference Include="Crestron.RAD.DeviceTypes.Security">
  <HintPath>C:\CrestronSDK\Crestron.RAD.DeviceTypes.Security.dll</HintPath>
</Reference>
```

## שלב 3 — להתאים את הקישור ל-SDK ⚠️
פתח את `src/ParadoxPrt3SecurityDriver.cs`. הוא בנוי כתבנית לפי מבנה ה-CCD,
אך **חתימות מחלקות הבסיס משתנות בין גרסאות SDK**. עליך:

1. לאמת את שם מחלקת הבסיס לסוג Security (לרוב `ASecurityProtocol` /
   `ASecurity`) ואת ה-constructor הנדרש — מול **Sample Projects → Security**
   ב-SDK.
2. למפות את האירועים שלנו ל-API של ה-SDK:
   - `OnZoneChanged` → עדכון מצב Zone (Open/Closed) של ה-SDK.
   - `OnAreaChanged` → עדכון מצב Partition (Armed/Disarmed/Alarm).
3. למפות `Arm`/`Disarm` של ה-SDK ל-`_engine.Arm()/Disarm()`.
4. לבחור ארכיטקטורת תעבורה (ראה למטה).

### שתי ארכיטקטורות תעבורה — בחר אחת
- **א. תעבורת TCP עצמאית (`ParadoxTcpTransport`)** — הדרייבר פותח בעצמו
  socket אל הממיר. פשוט, ומנוע ה-polling עובד כמו שהוא. מתאים כשהדרייבר
  מקבל Host/Port מההגדרות.
- **ב. תעבורת ה-SDK** — ה-SDK מספק תעבורה (Ethernet/Serial) ומזרים נתונים
  ב-event (DataHandler). במצב זה חבר את ה-DataHandler ישירות ל-
  `ParadoxProtocol.Parse(...)` במקום ה-polling-byte, ושמור על ה-`RZ/RA`
  כשאילתות תקופתיות מתוך ה-timer של ה-SDK.

  > עבור Crestron Home, אישור (certification) רשמי לרוב מצפה לתעבורת ה-SDK.
  > לשימוש פנימי/בית פרטי — תעבורת ה-TCP העצמאית עובדת מצוין.

## שלב 4 — קמפול
```
Build → Release
```
הפלט: `Mila_SecuritySystem_Paradox_PRT3_IP.dll` (+ תלויות).

## שלב 5 — אריזה ל-`.pkg` עם ManifestUtil
1. העתק את ה-DLL וכל התלויות לתיקייה של `ManifestUtil.exe`.
2. הרץ `ManifestUtil.exe` (דאבל-קליק).
3. אם הבנייה תקינה — ייווצר **`Mila_SecuritySystem_Paradox_PRT3_IP.pkg`**.

> מוסכמת שם הקובץ של Crestron: `<Developer>_<DeviceType>_<Manufacturer>_<BaseModel>_<Communication>`
> ולכן: `Mila_SecuritySystem_Paradox_PRT3_IP`.

## שלב 6 — חתימה/הסמכה (לפי המסלול)
- **בית פרטי / שימוש עצמי:** ניתן לטעון את ה-`.pkg` ישירות ל-Crestron Home
  (ראה `docs/06`).
- **הפצה מסחרית:** יש להגיש את הדרייבר ל-Crestron לאישור (Submit a Driver
  בפורטל המפתחים).

## פתרון תקלות בנייה
| תקלה | פתרון |
|------|-------|
| `type or namespace 'Crestron.RAD' not found` | ה-Reference ל-SDK חסר/שגוי HintPath. |
| הקוד תלוי-SDK לא מתקמפל | ודא שהוגדר הסמל `CRESTRON_RAD` ושהחתימות תואמות לגרסת ה-SDK. |
| ManifestUtil נכשל | חסרות תלויות בתיקייה, או שם הקובץ לא לפי המוסכמה. |
