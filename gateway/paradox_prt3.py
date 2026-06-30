"""
paradox_prt3.py
================

מימוש נקי ובדוק של פרוטוקול ה-ASCII של מודול Paradox APR-PRT3
(Printer Module) במצב Home Automation.

המודול הזה משמש לשתי מטרות:

1. **כלי אבחון/הרצה עצמאי** ("gateway") שניתן להריץ על מחשב/Raspberry Pi
   המחובר ל-PRT3, כדי לאמת את החיווט ואת הגדרות ה-PRT3 *לפני* שמתחילים
   לעבוד מול Crestron Home, וגם כפתרון גישור מלא (תנועה -> אור -> טיימר).

2. **מפרט בר-הרצה (executable specification)** של לוגיקת הפענוח. אותה
   לוגיקה בדיוק ממומשת בדרייבר ה-C# של Crestron (תיקיית `driver/`).
   קובץ הבדיקות `test_paradox_prt3.py` מאמת את הפענוח מול הודעות אמת.

הפרוטוקול (מאומת מול תיעוד Paradox "APR3-PRT3 ASCII Protocol" ומול
מימושים פתוחים):

* תקשורת טורית RS-232: 57600/19200/9600/2400 8N1 (ברירת מחדל מומלצת 57600).
  ניתן לעבוד גם מעל TCP דרך ממיר Serial<->Ethernet.
* כל הודעה (בקשה ותשובה) מסתיימת ב-CR יחיד (ASCII 13, '\r').
* כל פקודה מאומתת ב-echo: חמשת התווים הראשונים של הפקודה ואחריהם
  "&ok" לפקודה תקינה, או "&fail" לפקודה שגויה. כשהפקודה היא בקשת מידע
  (RZ/RA) — התשובה מכילה את המידע עצמו.

קודי פקודות עיקריים בהם אנו משתמשים:

* בקשת סטטוס גלאי/אזור-גילוי:   ``RZnnn``      (nnn = מספר הזון, 3 ספרות)
* בקשת סטטוס מחיצה (partition):  ``RAnnn``      (nnn = מספר המחיצה)
* דריכה:                          ``AAnnn<m><PIN>`` (m = A/F/S/I)
* נטרול:                          ``ADnnn<PIN>``

פורמט תשובת RZ:  ``RZnnn<s>....``  כאשר <s> הוא תו סטטוס:
    C = Closed (סגור/תקין)   O = Open (פתוח/תנועה)
    T = Tamper               F = Fire loop

פורמט תשובת RA:  ``RAnnn<arm>....<alarm>.`` כאשר:
    arm:   D = Disarmed (מנוטרל)   A = Armed (דרוך)
    alarm: O = Ok (אין אזעקה)      A = In Alarm (באזעקה)
"""

from __future__ import annotations

import enum
import logging
import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Dict, Optional

LOG = logging.getLogger("paradox.prt3")

CR = "\r"
ACK_OK = "&ok"
ACK_FAIL = "&fail"

DEFAULT_TCP_PORT = 10001  # פורט נפוץ בממירי Serial<->Ethernet (Lantronix וכו')


# --------------------------------------------------------------------------- #
# מצבים (enums)
# --------------------------------------------------------------------------- #
class ZoneState(enum.Enum):
    """מצב גלאי / זון בודד."""

    CLOSED = "closed"   # סגור / ללא תנועה / תקין
    OPEN = "open"       # פתוח / תנועה זוהתה
    TAMPER = "tamper"   # ניתוק/חבלה
    FIRE = "fire"       # לולאת אש
    UNKNOWN = "unknown"

    @classmethod
    def from_char(cls, c: str) -> "ZoneState":
        return {
            "C": cls.CLOSED,
            "O": cls.OPEN,
            "T": cls.TAMPER,
            "F": cls.FIRE,
        }.get(c.upper(), cls.UNKNOWN)

    @property
    def is_motion(self) -> bool:
        """True כשהגלאי מדווח על פתיחה/תנועה (הטריגר להדלקת אור)."""
        return self in (ZoneState.OPEN, ZoneState.TAMPER, ZoneState.FIRE)


class ArmState(enum.Enum):
    DISARMED = "disarmed"
    ARMED = "armed"
    UNKNOWN = "unknown"

    @classmethod
    def from_char(cls, c: str) -> "ArmState":
        return {"D": cls.DISARMED, "A": cls.ARMED}.get(c.upper(), cls.UNKNOWN)


class AlarmState(enum.Enum):
    OK = "ok"
    IN_ALARM = "in_alarm"
    UNKNOWN = "unknown"

    @classmethod
    def from_char(cls, c: str) -> "AlarmState":
        return {"O": cls.OK, "A": cls.IN_ALARM}.get(c.upper(), cls.UNKNOWN)


# --------------------------------------------------------------------------- #
# הודעות מפוענחות
# --------------------------------------------------------------------------- #
@dataclass(frozen=True)
class ZoneStatusMessage:
    zone: int
    state: ZoneState


@dataclass(frozen=True)
class AreaStatusMessage:
    area: int
    arm: ArmState
    alarm: AlarmState


@dataclass(frozen=True)
class AckMessage:
    command: str  # 5 התווים הראשונים של הפקודה ש-PRT3 מהדהד
    ok: bool


@dataclass(frozen=True)
class UnknownMessage:
    raw: str


ParsedMessage = object  # אחד מהטיפוסים שלמעלה


# --------------------------------------------------------------------------- #
# פענוח (פונקציות טהורות — נבדקות ביחידה)
# --------------------------------------------------------------------------- #
def build_zone_status_request(zone: int) -> str:
    """בונה פקודת בקשת סטטוס גלאי, ללא ה-CR הסופי."""
    if not 1 <= zone <= 999:
        raise ValueError(f"zone out of range: {zone}")
    return f"RZ{zone:03d}"


def build_area_status_request(area: int) -> str:
    if not 1 <= area <= 999:
        raise ValueError(f"area out of range: {area}")
    return f"RA{area:03d}"


def build_arm_command(area: int, pin: str, mode: str = "A") -> str:
    """דריכה. mode: A=רגיל, F=Force, S=Stay/חלקי, I=Instant."""
    mode = mode.upper()
    if mode not in ("A", "F", "S", "I"):
        raise ValueError(f"invalid arm mode: {mode}")
    return f"AA{area:03d}{mode}{pin}"


def build_disarm_command(area: int, pin: str) -> str:
    return f"AD{area:03d}{pin}"


def parse_message(raw: str) -> ParsedMessage:
    """
    מפענח שורת תשובה בודדת מ-PRT3 (ללא ה-CR).
    מחזיר אחד מ: ZoneStatusMessage / AreaStatusMessage / AckMessage /
    UnknownMessage.

    שים לב: אישור echo עשוי להגיע מחובר לתשובת המידע, ולכן בודקים
    את ה-prefix לפני ה-ack.
    """
    msg = raw.strip()
    if not msg:
        return UnknownMessage(raw=raw)

    # אישור (echo) טהור: "XXXXX&ok" / "XXXXX&fail"
    lowered = msg.lower()
    if ACK_OK in lowered or ACK_FAIL in lowered:
        # יתכן שיש מידע לפני ה-&ok? בפועל בקשות RZ/RA מחזירות מידע ולא &ok,
        # ולכן אם הגענו לכאן עם prefix RZ/RA ננסה קודם לפענח מידע.
        if not (msg.upper().startswith("RZ") or msg.upper().startswith("RA")):
            return AckMessage(command=msg[:5], ok=ACK_OK in lowered)

    upper = msg.upper()

    # תשובת סטטוס גלאי: RZnnn<s>
    if upper.startswith("RZ") and len(msg) >= 6 and msg[2:5].isdigit():
        # אם זו רק הדהוד פקודה ללא נתון (RZ001&ok) — נחזיר ack
        if "&" in msg:
            return AckMessage(command=msg[:5], ok=ACK_OK in lowered)
        zone = int(msg[2:5])
        return ZoneStatusMessage(zone=zone, state=ZoneState.from_char(msg[5]))

    # תשובת סטטוס מחיצה: RAnnn<arm>....<alarm>
    if upper.startswith("RA") and len(msg) >= 6 and msg[2:5].isdigit():
        if "&" in msg:
            return AckMessage(command=msg[:5], ok=ACK_OK in lowered)
        area = int(msg[2:5])
        arm = ArmState.from_char(msg[5]) if len(msg) > 5 else ArmState.UNKNOWN
        # תו האזעקה נמצא ב-offset 10 (RA + 3 ספרות + arm + 4 תווים + alarm)
        alarm = AlarmState.from_char(msg[10]) if len(msg) > 10 else AlarmState.UNKNOWN
        return AreaStatusMessage(area=area, arm=arm, alarm=alarm)

    return UnknownMessage(raw=msg)


# --------------------------------------------------------------------------- #
# שכבת תעבורה (transport) — TCP או טורי
# --------------------------------------------------------------------------- #
class Transport:
    """ממשק תעבורה מינימלי. ממומש ב-TcpTransport / SerialTransport."""

    def open(self) -> None: ...
    def close(self) -> None: ...
    def send_line(self, line: str) -> None: ...
    def read_byte(self) -> str:
        """מחזיר תו אחד, או '' אם לא הגיע דבר (timeout)."""
        ...


class TcpTransport(Transport):
    """תקשורת מעל ממיר Serial<->Ethernet (למשל Lantronix/USR/Moxa)."""

    def __init__(self, host: str, port: int = DEFAULT_TCP_PORT, timeout: float = 1.0):
        self.host = host
        self.port = port
        self.timeout = timeout
        self._sock: Optional[socket.socket] = None

    def open(self) -> None:
        self._sock = socket.create_connection((self.host, self.port), self.timeout)
        self._sock.settimeout(self.timeout)
        LOG.info("TCP connected to %s:%s", self.host, self.port)

    def close(self) -> None:
        if self._sock:
            try:
                self._sock.close()
            finally:
                self._sock = None

    def send_line(self, line: str) -> None:
        if not self._sock:
            raise RuntimeError("transport not open")
        self._sock.sendall((line + CR).encode("ascii"))

    def read_byte(self) -> str:
        if not self._sock:
            raise RuntimeError("transport not open")
        try:
            data = self._sock.recv(1)
        except socket.timeout:
            return ""
        if not data:
            raise ConnectionError("socket closed by peer")
        return data.decode("ascii", errors="ignore")


class SerialTransport(Transport):
    """תקשורת RS-232 ישירה (דורש pyserial: pip install pyserial)."""

    def __init__(self, port: str, baudrate: int = 57600, timeout: float = 1.0):
        self.port = port
        self.baudrate = baudrate
        self.timeout = timeout
        self._ser = None

    def open(self) -> None:
        import serial  # ייבוא עצל כדי לא לחייב pyserial לכלי שעובד ב-TCP

        self._ser = serial.Serial(
            self.port, self.baudrate, bytesize=8, parity="N",
            stopbits=1, timeout=self.timeout,
        )
        LOG.info("Serial opened %s @ %s 8N1", self.port, self.baudrate)

    def close(self) -> None:
        if self._ser:
            try:
                self._ser.close()
            finally:
                self._ser = None

    def send_line(self, line: str) -> None:
        if not self._ser:
            raise RuntimeError("transport not open")
        self._ser.write((line + CR).encode("ascii"))

    def read_byte(self) -> str:
        if not self._ser:
            raise RuntimeError("transport not open")
        data = self._ser.read(1)
        return data.decode("ascii", errors="ignore") if data else ""


# --------------------------------------------------------------------------- #
# הלקוח: polling של זונים ומחיצות + הפצת אירועי שינוי
# --------------------------------------------------------------------------- #
ZoneCallback = Callable[[int, ZoneState, Optional[ZoneState]], None]
AreaCallback = Callable[[int, AreaStatusMessage, Optional[AreaStatusMessage]], None]


@dataclass
class Prt3Config:
    zones: list = field(default_factory=list)       # מספרי גלאים לסקירה
    areas: list = field(default_factory=list)       # מספרי מחיצות לסקירה
    poll_interval: float = 0.4                       # שניות בין סבבי polling
    line_timeout: float = 1.0                        # timeout לקריאת שורה


class Prt3Client:
    """
    לקוח polling: מבקש סטטוס לכל גלאי/מחיצה מוגדרים, משווה למצב הקודם,
    ומפעיל callback רק כשמשהו השתנה. גישה זו אמינה ואינה תלויה במצב
    שידור-אירועים של ה-PRT3 (שמשתנה בין גרסאות קושחה).
    """

    def __init__(self, transport: Transport, config: Prt3Config):
        self.transport = transport
        self.config = config
        self._zone_state: Dict[int, ZoneState] = {}
        self._area_state: Dict[int, AreaStatusMessage] = {}
        self._on_zone: Optional[ZoneCallback] = None
        self._on_area: Optional[AreaCallback] = None
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._rx = ""  # buffer לתווים שטרם הושלמו לשורה

    # --- רישום מאזינים --- #
    def on_zone_change(self, cb: ZoneCallback) -> None:
        self._on_zone = cb

    def on_area_change(self, cb: AreaCallback) -> None:
        self._on_area = cb

    # --- מצב נוכחי (קריאה) --- #
    def zone_state(self, zone: int) -> ZoneState:
        return self._zone_state.get(zone, ZoneState.UNKNOWN)

    def area_status(self, area: int) -> Optional[AreaStatusMessage]:
        return self._area_state.get(area)

    # --- I/O בסיסי --- #
    def _read_line(self) -> Optional[str]:
        """קורא עד CR. מחזיר None אם לא הושלמה שורה בתוך ה-timeout."""
        deadline = time.monotonic() + self.config.line_timeout
        while time.monotonic() < deadline:
            ch = self.transport.read_byte()
            if ch == "":
                continue
            if ch in ("\r", "\n"):
                if self._rx:
                    line, self._rx = self._rx, ""
                    return line
                continue
            self._rx += ch
        return None

    def _request(self, command: str) -> Optional[ParsedMessage]:
        """שולח פקודה וקורא תשובה אחת רלוונטית."""
        self.transport.send_line(command)
        for _ in range(5):  # מדלגים על שורות לא רלוונטיות (echo וכו')
            line = self._read_line()
            if line is None:
                return None
            parsed = parse_message(line)
            if not isinstance(parsed, (UnknownMessage, AckMessage)):
                return parsed
        return None

    # --- סקירה והפצת אירועים --- #
    def poll_once(self) -> None:
        for zone in self.config.zones:
            msg = self._request(build_zone_status_request(zone))
            if isinstance(msg, ZoneStatusMessage):
                self._update_zone(msg)
        for area in self.config.areas:
            msg = self._request(build_area_status_request(area))
            if isinstance(msg, AreaStatusMessage):
                self._update_area(msg)

    def _update_zone(self, msg: ZoneStatusMessage) -> None:
        prev = self._zone_state.get(msg.zone)
        if prev != msg.state:
            self._zone_state[msg.zone] = msg.state
            LOG.debug("zone %s: %s -> %s", msg.zone, prev, msg.state)
            if self._on_zone:
                self._on_zone(msg.zone, msg.state, prev)

    def _update_area(self, msg: AreaStatusMessage) -> None:
        prev = self._area_state.get(msg.area)
        if prev != msg:
            self._area_state[msg.area] = msg
            LOG.debug("area %s: %s -> %s", msg.area, prev, msg)
            if self._on_area:
                self._on_area(msg.area, msg, prev)

    # --- ניהול thread רקע --- #
    def start(self) -> None:
        self.transport.open()
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self.poll_once()
            except (ConnectionError, OSError) as exc:
                LOG.warning("transport error: %s — reconnecting in 3s", exc)
                self._reconnect()
            self._stop.wait(self.config.poll_interval)

    def _reconnect(self) -> None:
        try:
            self.transport.close()
        except Exception:
            pass
        while not self._stop.is_set():
            try:
                self.transport.open()
                LOG.info("reconnected")
                return
            except OSError:
                self._stop.wait(3.0)

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2.0)
        self.transport.close()


# --------------------------------------------------------------------------- #
# לוגיקת תנועה -> אור -> טיימר (לשימוש בכלי הגישור העצמאי)
# --------------------------------------------------------------------------- #
@dataclass
class MotionRule:
    zone: int                 # הגלאי שמפעיל
    light_on: Callable[[], None]
    light_off: Callable[[], None]
    timeout_sec: float        # זמן עד כיבוי לאחר תנועה אחרונה
    only_when_armed_area: Optional[int] = None  # הפעל רק כשמחיצה זו דרוכה


class MotionLightController:
    """
    מנהל הכלל: כאשר גלאי עובר ל-OPEN (תנועה) -> מדליק אור ומאתחל טיימר.
    כל תנועה נוספת מאפסת את הטיימר. בתום הטיימר -> מכבה.

    זו הלוגיקה לכלי הגישור העצמאי. בתוך Crestron Home מומלץ לממש את
    אותו רעיון באמצעות "חיישן נוכחות" של החדר ו-Auto-Off המובנה
    (ראו docs/06 ו-07).
    """

    def __init__(self, client: Prt3Client):
        self.client = client
        self._rules: Dict[int, MotionRule] = {}
        self._timers: Dict[int, threading.Timer] = {}
        self._lock = threading.Lock()
        client.on_zone_change(self._handle_zone)

    def add_rule(self, rule: MotionRule) -> None:
        self._rules[rule.zone] = rule

    def _area_armed(self, area: Optional[int]) -> bool:
        if area is None:
            return True
        st = self.client.area_status(area)
        return st is not None and st.arm == ArmState.ARMED

    def _handle_zone(self, zone: int, state: ZoneState, prev) -> None:
        rule = self._rules.get(zone)
        if rule is None:
            return
        if state.is_motion and self._area_armed(rule.only_when_armed_area):
            self._trigger(rule)

    def _trigger(self, rule: MotionRule) -> None:
        with self._lock:
            existing = self._timers.pop(rule.zone, None)
            if existing:
                existing.cancel()
            else:
                LOG.info("motion on zone %s -> light ON", rule.zone)
                rule.light_on()
            timer = threading.Timer(rule.timeout_sec, self._expire, args=(rule,))
            timer.daemon = True
            self._timers[rule.zone] = timer
            timer.start()

    def _expire(self, rule: MotionRule) -> None:
        with self._lock:
            self._timers.pop(rule.zone, None)
        LOG.info("timer expired zone %s -> light OFF", rule.zone)
        rule.light_off()

    def shutdown(self) -> None:
        with self._lock:
            for t in self._timers.values():
                t.cancel()
            self._timers.clear()
