# אינטגרציית Paradox → Crestron Home (CP4-R)

מודול/דרייבר ומדריך הטמעה לקריאת מצב גלאים ואזעקות ממערכת **Paradox** אל בקר
**Crestron CP4-R** (Crestron Home OS), והפעלת תאורה עם טיימר כיבוי בתגובה לתנועה.

> **המטרה:** כשגלאי תנועה באזור מגלה תנועה → מדליקים אור ב-Crestron ומתחילים
> ספירת זמן; בתום הטיימר → מכבים את התאורה. בנוסף, חשיפת מצבי פתוח/סגור ואזעקה
> של המערכת אל Crestron Home.

---

## איך זה עובד — בקצרה

Crestron CP4-R הוא בקר **Crestron Home** — לא ניתן לטעון עליו תוכנית SIMPL מותאמת.
הדרך הנתמכת היחידה לחבר התקן צד-שלישי היא באמצעות **Crestron Certified Driver (`.pkg`)**.
לכן הפתרון בנוי משני חלקים:

```
┌──────────────┐   RS-232 ASCII    ┌───────────────────────┐   TCP/IP    ┌──────────────┐
│  לוח Paradox  │ ◄───────────────► │  מודול APR-PRT3        │ ◄─────────► │  Crestron     │
│ (EVO/SP/MG)  │   (Home Automation│  + ממיר Serial↔Ethernet│   ASCII     │  CP4-R        │
│              │    ASCII protocol)│  (Lantronix/Moxa/USR)  │  over TCP   │  + הדרייבר    │
└──────────────┘                   └───────────────────────┘             └──────────────┘
                                                                                │
                                                              גלאים → Zones/Sensors
                                                              מחיצות → Security Partitions
                                                                                │
                                                              ┌─────────────────▼─────────────┐
                                                              │ מנוע האוטומציה של Crestron Home │
                                                              │  תנועה → הדלקת אור → טיימר כיבוי │
                                                              └───────────────────────────────┘
```

הדרייבר **קורא** מ-PRT3 (polling יציב של פקודות `RZ`/`RA`), מזהה שינויים, וחושף:
- כל **גלאי** כ-Zone/Sensor (פתוח/סגור/תנועה).
- כל **מחיצה** כ-Security Partition (דרוך/מנוטרל/אזעקה).

את לוגיקת **"תנועה → אור → טיימר"** בונים במנוע האוטומציה של Crestron Home
(Auto-Off של החדר), כך שהיא שקופה, ניתנת לעריכה ע"י הלקוח, ולא "קבורה" בקוד.
מי שמעדיף — יכול לממש את הטיימר גם בתוך הדרייבר; שתי הדרכים מתועדות.

---

## מבנה ה-Repository

```
.
├── README.md                  ← אתה כאן (סקירה + תוכן עניינים)
├── docs/                      ← מדריך ההטמעה המלא (קרא לפי הסדר)
│   ├── 01-architecture.md         אדריכלות, רכיבים, החלטות תכן
│   ├── 02-hardware-wiring.md      חיווט PRT3 ↔ ממיר IP ↔ CP4-R
│   ├── 03-prt3-programming.md     תכנות מודול ה-PRT3
│   ├── 04-protocol-reference.md   פירוט פרוטוקול ה-ASCII
│   ├── 05-build-crestron-driver.md  בניית קובץ ה-.pkg עם ה-SDK
│   ├── 06-crestron-home-setup.md  הוספת הדרייבר ומיפוי אזורים ב-Setup App
│   └── 07-motion-light-timer.md   בניית אוטומציית תנועה→אור→טיימר
│
├── driver/                    ← הדרייבר ל-Crestron Home (C#)
│   ├── src/
│   │   ├── ParadoxProtocol.cs            פענוח/בניית פקודות (ללא תלות SDK)
│   │   ├── Prt3Engine.cs                 מנוע polling + אירועי שינוי
│   │   ├── ParadoxTcpTransport.cs        תעבורת TCP
│   │   └── ParadoxPrt3SecurityDriver.cs  קישור ל-CCD SDK (Security device)
│   ├── DriverData/…                      מטא-דאטה של הדרייבר
│   └── Mila_SecuritySystem_Paradox_PRT3_IP.csproj
│
└── gateway/                   ← מימוש Python מלא + בדיקות (אותו פרוטוקול)
    ├── paradox_prt3.py            ליבת הפרוטוקול + לוגיקת תנועה→אור→טיימר
    ├── cli.py                     כלי אבחון/הרצה עצמאי
    ├── test_paradox_prt3.py       22 בדיקות יחידה (עוברות)
    ├── config.example.json        קונפיג לדוגמה
    └── requirements.txt
```

---

## שני מסלולי שימוש

### מסלול א' — Crestron Home נייטיב (מומלץ)
הדרייבר נטען ל-Crestron Home, הגלאים מופיעים כ-Zones/Sensors, ואת ההפעלה
(אור+טיימר) בונים ב-Setup App. **זה מה שביקשת.** ראה `docs/05` → `docs/07`.

### מסלול ב' — כלי גישור עצמאי (לאבחון, או כפתרון על Raspberry Pi)
לפני שנוגעים ב-Crestron, מומלץ להריץ את כלי ה-Python כדי לוודא שהחיווט
והגדרות ה-PRT3 תקינים ושמצבי הגלאים מתקבלים:

```bash
cd gateway
python3 cli.py monitor --tcp 192.168.1.50:10001 --zones 1-8 --areas 1
```

תקבל הדפסה חיה של כל שינוי מצב גלאי/מחיצה. אותו כלי יכול גם להריץ בפועל
את לוגיקת התנועה→אור→טיימר (`python3 cli.py run --config config.json`),
שימושי אם בוחרים לבצע את הגישור על מחשב קטן צמוד ל-PRT3.

---

## בדיקת תקינות הפרוטוקול

ליבת הפענוח מאומתת ב-22 בדיקות יחידה:

```bash
cd gateway
python3 -m unittest test_paradox_prt3 -v   # 22 passed
```

לוגיקת ה-C# בדרייבר (`ParadoxProtocol.cs`) ממומשת 1:1 לפי אותה התנהגות בדוקה.

---

## דרישות מקדימות

| רכיב | פירוט |
|------|-------|
| לוח Paradox | סדרת EVO / SP / MG / DGP עם מודול **APR-PRT3** |
| APR-PRT3 | מודול Printer/ASCII — חובה. **IP150 לבדו אינו מספיק** (פרוטוקול סגור) |
| ממיר Serial↔Ethernet | לחיבור IP (Lantronix UDS/XPort, Moxa NPort, USR-TCP232) — אם לא מחברים טורי ישיר |
| Crestron | CP4-R עם Crestron Home, וגישת **Setup App** |
| לבניית הדרייבר | Visual Studio 2022 Community (חינם) + 2 חבילות NuGet של Crestron. ה-`.pkg` נוצר אוטומטית. מדריך מלא "מאפס" ב-`docs/05` ובפרק 5 של מדריך ה-HTML |

> אם אין לך סביבת SDK של Crestron לקמפול — קבצי המקור כאן מלאים ומוכנים;
> שלבי הבנייה והאריזה ל-`.pkg` מפורטים ב-`docs/05`.

---

## מקורות

- Paradox APR3-PRT3 — ASCII Protocol Programming Instructions
- Crestron Drivers SDK — https://sdkcon78221.crestron.com/
- Crestron Home OS Documentation (CP4-R, Third-Party Drivers) — https://docs.crestron.com/
