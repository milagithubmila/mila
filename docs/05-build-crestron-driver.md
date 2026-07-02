# 05 — בניית הדרייבר ל-Crestron Home (קובץ `.pkg`) — מאפס

הופך את קוד ה-C# שב-`driver/` לקובץ **`.pkg`** שנטען ל-Crestron Home.
הפרק כתוב למי שמעולם לא פתח סביבת פיתוח.

> **גרסה מעוצבת ומלאה יותר:** ראה `docs/מדריך-Paradox-Crestron.html`, פרק 5.

## לפני שמתחילים — 3 אפשרויות
| אפשרות | למי | מאמץ |
|--------|-----|------|
| **א. לבנות בעצמך** | מוכן ללמוד סביבת פיתוח בסיסית (כמה שעות, פעם אחת) | בינוני |
| **ב. למסור למתכנת Crestron** | שולחים את תיקיית `driver/` לאינטגרטור — ~30 דק' עבורו | נמוך |
| **ג. בלי דרייבר** | Raspberry Pi + כלי ה-Python ששולט בתאורה ישירות | נמוך-בינוני |

אם בחרת ב' — דלג ל-`docs/06`. אם א' — המשך.

## מילון מושגים
- **Visual Studio** — העורך שבו בונים קוד (חינם, Community).
- **`.csproj`** — "מתכון" הפרויקט. כבר מוכן עבורך.
- **NuGet** — "חנות תוספים" מובנית; מתקינים חבילה בלחיצה.
- **SDK** — רכיבי Crestron לבניית דרייברים; מגיע כחבילות NuGet.
- **`.pkg`** — קובץ הדרייבר הסופי; **נוצר אוטומטית** בסוף הבנייה.

## שלב 0 — הורדות
| מה | מאיפה | עלות |
|----|-------|------|
| **Visual Studio 2022 Community** (סמן Workload: **.NET desktop development**) | visualstudio.microsoft.com | חינם |
| **חשבון מפתחים ב-Crestron** (לתיעוד/דוגמאות; חבילות ה-NuGet ציבוריות) | sdkcon78221.crestron.com | חינם |

> חבילות ה-NuGet של Crestron נמצאות ב-nuget.org הציבורי — אין צורך בהתחברות מיוחדת.

## בנייה — צעד אחר צעד
1. **התקן Visual Studio 2022 Community** עם Workload **".NET desktop development"**.
2. **פתח את הפרויקט:** `File → Open → Project/Solution` →
   `driver/Mila_SecuritySystem_Paradox_PRT3_IP.csproj`.
3. **חבילות ה-NuGet** מוגדרות כבר בקובץ הפרויקט ויותקנו אוטומטית (Restore).
   אם לא: `Tools → NuGet Package Manager → Manage NuGet Packages` → Browse →
   התקן:
   - `Crestron.DeviceDrivers.DevKit` — כל רכיבי ה-SDK.
   - `Crestron.DeviceDrivers.ManifestUtil` — יוצר את ה-`.pkg` אוטומטית.
4. **בחר `Release`** בתיבה בסרגל העליון (במקום `Debug`).
5. **בנה:** `Build → Build Solution` (Ctrl+Shift+B). המתן ל-`Build: 1 succeeded`.
6. **קח את ה-`.pkg`** מ-`driver/bin/Release/…` →
   `Mila_SecuritySystem_Paradox_PRT3_IP.pkg`.

> **אין צורך** לערוך `HintPath` או להריץ ManifestUtil ידנית — חבילות ה-NuGet
> מטפלות בהכל. (זו הדרך המודרנית; מדריכים ישנים מתארים DLL ידני — התעלם מהם.)

## מבנה הקוד
| קובץ | תלות SDK | הערה |
|------|:--------:|------|
| `src/ParadoxProtocol.cs` | ❌ | פענוח/בניית פקודות — מתקמפל לבד |
| `src/Prt3Engine.cs` | ❌ | polling + אירועי שינוי — מתקמפל לבד |
| `src/ParadoxTcpTransport.cs` | ❌ | TCP (System.Net.Sockets) |
| `src/ParadoxPrt3SecurityDriver.cs` | ✅ | עטוף ב-`#if CRESTRON_RAD` |

## פתרון תקלות בנייה
| תקלה | פתרון |
|------|-------|
| שגיאות `Crestron.RAD...` בקובץ `ParadoxPrt3SecurityDriver.cs` | חתימות ה-SDK תלויות-גרסה. התאם מול **Samples → Security** של ה-SDK. זו הנקודה היחידה שדורשת עין של מתכנת — מועמדת טובה למסירה (אפשרות ב'). |
| החבילות לא הותקנו | הרץ Restore: לחיצה ימנית על ה-Solution → Restore NuGet Packages. |
| Crestron Home מסרב לטעון דרייבר עצמי | ייתכן שנדרשת הסמלה/הסמכה — ראה למטה. |

## הסמכה / הפצה (רק אם צריך)
- **שימוש עצמי / לקוח שלך:** בונים `.pkg` וטוענים ישירות (ראה `docs/06`).
  **אין צורך להעלות ל-Crestron.**
- **הפצה מסחרית לכלל המתקינים:** מגישים ל-Crestron ב-"Submit a Driver"
  לתהליך אישור וחתימה.
