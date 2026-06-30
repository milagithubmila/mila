"""
בדיקות יחידה לפרוטוקול PRT3.
הרצה:  python -m unittest gateway/test_paradox_prt3.py -v
        (או:  cd gateway && python -m unittest test_paradox_prt3 -v)
"""
import threading
import time
import unittest

import paradox_prt3 as p


class TestBuilders(unittest.TestCase):
    def test_zone_request_zero_padded(self):
        self.assertEqual(p.build_zone_status_request(1), "RZ001")
        self.assertEqual(p.build_zone_status_request(42), "RZ042")
        self.assertEqual(p.build_zone_status_request(128), "RZ128")

    def test_area_request_zero_padded(self):
        self.assertEqual(p.build_area_status_request(1), "RA001")

    def test_zone_request_out_of_range(self):
        with self.assertRaises(ValueError):
            p.build_zone_status_request(0)
        with self.assertRaises(ValueError):
            p.build_zone_status_request(1000)

    def test_arm_disarm_commands(self):
        self.assertEqual(p.build_arm_command(1, "1234", "A"), "AA001A1234")
        self.assertEqual(p.build_arm_command(2, "0000", "S"), "AA002S0000")
        self.assertEqual(p.build_disarm_command(1, "1234"), "AD0011234")

    def test_arm_invalid_mode(self):
        with self.assertRaises(ValueError):
            p.build_arm_command(1, "1234", "X")


class TestZoneParsing(unittest.TestCase):
    def test_zone_closed(self):
        m = p.parse_message("RZ001C")
        self.assertIsInstance(m, p.ZoneStatusMessage)
        self.assertEqual(m.zone, 1)
        self.assertEqual(m.state, p.ZoneState.CLOSED)
        self.assertFalse(m.state.is_motion)

    def test_zone_open_is_motion(self):
        m = p.parse_message("RZ005O")
        self.assertEqual(m.zone, 5)
        self.assertEqual(m.state, p.ZoneState.OPEN)
        self.assertTrue(m.state.is_motion)

    def test_zone_with_trailing_flags(self):
        # חלק מהקושחות מחזירות תווי דגלים נוספים אחרי תו הסטטוס
        m = p.parse_message("RZ012Ooooo")
        self.assertEqual(m.zone, 12)
        self.assertEqual(m.state, p.ZoneState.OPEN)

    def test_zone_tamper_and_fire(self):
        self.assertEqual(p.parse_message("RZ003T").state, p.ZoneState.TAMPER)
        self.assertEqual(p.parse_message("RZ004F").state, p.ZoneState.FIRE)

    def test_zone_cr_lf_stripped(self):
        m = p.parse_message("RZ007O\r\n")
        self.assertEqual(m.zone, 7)
        self.assertEqual(m.state, p.ZoneState.OPEN)


class TestAreaParsing(unittest.TestCase):
    def test_area_disarmed_ok(self):
        # RA + 001 + D(arm) + 4 תווים + O(alarm) + תו
        m = p.parse_message("RA001D----O-")
        self.assertIsInstance(m, p.AreaStatusMessage)
        self.assertEqual(m.area, 1)
        self.assertEqual(m.arm, p.ArmState.DISARMED)
        self.assertEqual(m.alarm, p.AlarmState.OK)

    def test_area_armed_in_alarm(self):
        m = p.parse_message("RA002A----A-")
        self.assertEqual(m.area, 2)
        self.assertEqual(m.arm, p.ArmState.ARMED)
        self.assertEqual(m.alarm, p.AlarmState.IN_ALARM)

    def test_area_short_message_unknown_alarm(self):
        m = p.parse_message("RA001A")
        self.assertEqual(m.arm, p.ArmState.ARMED)
        self.assertEqual(m.alarm, p.AlarmState.UNKNOWN)


class TestAckParsing(unittest.TestCase):
    def test_ack_ok(self):
        m = p.parse_message("AA001&ok")
        self.assertIsInstance(m, p.AckMessage)
        self.assertTrue(m.ok)

    def test_ack_fail(self):
        m = p.parse_message("AD001&fail")
        self.assertIsInstance(m, p.AckMessage)
        self.assertFalse(m.ok)

    def test_rz_ack_not_treated_as_data(self):
        m = p.parse_message("RZ001&ok")
        self.assertIsInstance(m, p.AckMessage)

    def test_empty(self):
        self.assertIsInstance(p.parse_message(""), p.UnknownMessage)
        self.assertIsInstance(p.parse_message("garbage"), p.UnknownMessage)


# --------------------------------------------------------------------------- #
# Transport מדומה לבדיקות אינטגרציה של הלקוח ושל לוגיקת הטיימר
# --------------------------------------------------------------------------- #
class FakeTransport(p.Transport):
    """מגיב לפקודות RZ/RA ממילון מצב נתון, ניתן לשינוי בזמן ריצה."""

    def __init__(self):
        self.zone_chars = {}   # zone -> 'C'/'O'/...
        self.area_resp = {}    # area -> מחרוזת תשובה מלאה
        self._out = ""
        self._lock = threading.Lock()
        self.opened = False

    def open(self):
        self.opened = True

    def close(self):
        self.opened = False

    def send_line(self, line):
        with self._lock:
            if line.startswith("RZ"):
                z = int(line[2:5])
                ch = self.zone_chars.get(z, "C")
                self._out += f"RZ{z:03d}{ch}\r"
            elif line.startswith("RA"):
                a = int(line[2:5])
                resp = self.area_resp.get(a, f"RA{a:03d}D----O-")
                self._out += resp + "\r"

    def read_byte(self):
        with self._lock:
            if self._out:
                ch, self._out = self._out[0], self._out[1:]
                return ch
        return ""


class TestClientPolling(unittest.TestCase):
    def test_zone_change_callback_fires_once_per_change(self):
        ft = FakeTransport()
        ft.zone_chars = {1: "C"}
        cfg = p.Prt3Config(zones=[1], areas=[], poll_interval=0.01, line_timeout=0.2)
        client = p.Prt3Client(ft, cfg)

        events = []
        client.on_zone_change(lambda z, s, prev: events.append((z, s)))

        ft.open()
        client.poll_once()                       # מצב התחלתי C -> אירוע ראשון
        ft.zone_chars[1] = "O"
        client.poll_once()                       # שינוי ל-O -> אירוע שני
        client.poll_once()                       # ללא שינוי -> אין אירוע

        self.assertEqual(events, [(1, p.ZoneState.CLOSED), (1, p.ZoneState.OPEN)])

    def test_area_state_tracked(self):
        ft = FakeTransport()
        ft.area_resp = {1: "RA001A----O-"}
        cfg = p.Prt3Config(zones=[], areas=[1], poll_interval=0.01, line_timeout=0.2)
        client = p.Prt3Client(ft, cfg)
        ft.open()
        client.poll_once()
        st = client.area_status(1)
        self.assertEqual(st.arm, p.ArmState.ARMED)


class TestMotionLightController(unittest.TestCase):
    def test_motion_turns_on_then_off_after_timeout(self):
        ft = FakeTransport()
        ft.zone_chars = {10: "C"}
        cfg = p.Prt3Config(zones=[10], areas=[], poll_interval=0.01, line_timeout=0.2)
        client = p.Prt3Client(ft, cfg)
        controller = p.MotionLightController(client)

        light = {"on": 0, "off": 0}
        controller.add_rule(p.MotionRule(
            zone=10,
            light_on=lambda: light.__setitem__("on", light["on"] + 1),
            light_off=lambda: light.__setitem__("off", light["off"] + 1),
            timeout_sec=0.3,
        ))

        ft.open()
        client.poll_once()                 # C התחלתי
        ft.zone_chars[10] = "O"
        client.poll_once()                 # תנועה -> אור נדלק
        self.assertEqual(light["on"], 1)
        self.assertEqual(light["off"], 0)

        time.sleep(0.45)                   # מחכים שהטיימר יפוג
        self.assertEqual(light["off"], 1)
        controller.shutdown()

    def test_repeated_motion_resets_timer(self):
        ft = FakeTransport()
        ft.zone_chars = {10: "C"}
        cfg = p.Prt3Config(zones=[10], areas=[], poll_interval=0.01, line_timeout=0.2)
        client = p.Prt3Client(ft, cfg)
        controller = p.MotionLightController(client)

        light = {"on": 0, "off": 0}
        controller.add_rule(p.MotionRule(
            zone=10,
            light_on=lambda: light.__setitem__("on", light["on"] + 1),
            light_off=lambda: light.__setitem__("off", light["off"] + 1),
            timeout_sec=0.3,
        ))
        ft.open()
        client.poll_once()
        ft.zone_chars[10] = "O"
        client.poll_once()                 # תנועה 1 -> ON
        time.sleep(0.15)
        ft.zone_chars[10] = "C"
        client.poll_once()
        ft.zone_chars[10] = "O"
        client.poll_once()                 # תנועה 2 -> מאפס טיימר (לא מדליק שוב)
        self.assertEqual(light["on"], 1)
        time.sleep(0.2)                    # סה"כ 0.35 מתנועה 1, אך רק 0.2 מ-2
        self.assertEqual(light["off"], 0)  # עדיין דולק כי הטיימר אופס
        time.sleep(0.2)
        self.assertEqual(light["off"], 1)
        controller.shutdown()

    def test_only_when_armed_gate(self):
        ft = FakeTransport()
        ft.zone_chars = {10: "C"}
        ft.area_resp = {1: "RA001D----O-"}   # מנוטרל
        cfg = p.Prt3Config(zones=[10], areas=[1], poll_interval=0.01, line_timeout=0.2)
        client = p.Prt3Client(ft, cfg)
        controller = p.MotionLightController(client)

        light = {"on": 0}
        controller.add_rule(p.MotionRule(
            zone=10,
            light_on=lambda: light.__setitem__("on", light["on"] + 1),
            light_off=lambda: None,
            timeout_sec=0.3,
            only_when_armed_area=1,
        ))
        ft.open()
        client.poll_once()
        ft.zone_chars[10] = "O"
        client.poll_once()                 # תנועה אך המחיצה מנוטרלת -> אין אור
        self.assertEqual(light["on"], 0)
        controller.shutdown()


if __name__ == "__main__":
    unittest.main(verbosity=2)
