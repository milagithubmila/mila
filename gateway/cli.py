#!/usr/bin/env python3
"""
cli.py — כלי גישור/אבחון עצמאי ל-Paradox PRT3.

שני מצבים:

1. monitor — מתחבר ל-PRT3, סוקר גלאים/מחיצות ומדפיס כל שינוי מצב.
   מצוין כדי לאמת חיווט והגדרות PRT3 *לפני* שמתחילים עם Crestron.

2. run — מריץ את כללי "תנועה -> אור -> טיימר" מתוך config.json.
   במצב זה הדלקת/כיבוי האור מבוצעים דרך פקודת shell (light_on_cmd /
   light_off_cmd) — למשל curl ל-API של רכיב תאורה, או כל פקודה אחרת.

דוגמאות:
    python3 cli.py monitor --tcp 192.168.1.50:10001 --zones 1-8 --areas 1
    python3 cli.py monitor --serial /dev/ttyUSB0 --baud 57600 --zones 1,2,3
    python3 cli.py run --config config.json
"""
import argparse
import json
import logging
import subprocess
import sys
import time

import paradox_prt3 as p


def parse_id_list(spec: str):
    """'1-8,12,20' -> [1,2,3,4,5,6,7,8,12,20]"""
    out = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-")
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def make_transport(args) -> p.Transport:
    if args.tcp:
        host, _, port = args.tcp.partition(":")
        return p.TcpTransport(host, int(port or p.DEFAULT_TCP_PORT))
    if args.serial:
        return p.SerialTransport(args.serial, args.baud)
    raise SystemExit("חובה לציין --tcp host:port או --serial /dev/ttyUSBx")


def cmd_monitor(args):
    transport = make_transport(args)
    cfg = p.Prt3Config(
        zones=parse_id_list(args.zones) if args.zones else [],
        areas=parse_id_list(args.areas) if args.areas else [],
        poll_interval=args.interval,
    )
    client = p.Prt3Client(transport, cfg)

    client.on_zone_change(lambda z, s, prev:
                          print(f"[ZONE {z:03d}] {prev} -> {s.value.upper()}"))
    client.on_area_change(lambda a, m, prev:
                          print(f"[AREA {a:03d}] arm={m.arm.value} alarm={m.alarm.value}"))

    print(f"מנטר גלאים={cfg.zones} מחיצות={cfg.areas} ... Ctrl+C ליציאה")
    client.start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        client.stop()


def cmd_run(args):
    with open(args.config, encoding="utf-8") as fh:
        conf = json.load(fh)

    if conf.get("transport", {}).get("type") == "serial":
        transport = p.SerialTransport(conf["transport"]["port"],
                                      conf["transport"].get("baud", 57600))
    else:
        t = conf["transport"]
        transport = p.TcpTransport(t["host"], t.get("port", p.DEFAULT_TCP_PORT))

    zones = sorted({r["zone"] for r in conf["rules"]})
    areas = sorted({r["only_when_armed_area"] for r in conf["rules"]
                    if r.get("only_when_armed_area")})
    cfg = p.Prt3Config(zones=zones, areas=areas,
                       poll_interval=conf.get("poll_interval", 0.4))
    client = p.Prt3Client(transport, cfg)
    controller = p.MotionLightController(client)

    def shell(cmd):
        return lambda: subprocess.run(cmd, shell=True, check=False)

    for r in conf["rules"]:
        controller.add_rule(p.MotionRule(
            zone=r["zone"],
            light_on=shell(r["light_on_cmd"]),
            light_off=shell(r["light_off_cmd"]),
            timeout_sec=r["timeout_sec"],
            only_when_armed_area=r.get("only_when_armed_area"),
        ))
        print(f"כלל: גלאי {r['zone']} -> אור (timeout {r['timeout_sec']}s)")

    client.start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        controller.shutdown()
        client.stop()


def main():
    ap = argparse.ArgumentParser(description="Paradox PRT3 gateway/diagnostic")
    ap.add_argument("-v", "--verbose", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)

    m = sub.add_parser("monitor", help="נטר ומדפיס שינויי מצב")
    m.add_argument("--tcp", help="host:port של ממיר Serial<->Ethernet")
    m.add_argument("--serial", help="התקן טורי, למשל /dev/ttyUSB0")
    m.add_argument("--baud", type=int, default=57600)
    m.add_argument("--zones", help="רשימת גלאים, למשל 1-8,12")
    m.add_argument("--areas", help="רשימת מחיצות, למשל 1,2")
    m.add_argument("--interval", type=float, default=0.4)
    m.set_defaults(func=cmd_monitor)

    r = sub.add_parser("run", help="הרץ כללי תנועה->אור->טיימר מ-config.json")
    r.add_argument("--config", required=True)
    r.set_defaults(func=cmd_run)

    args = ap.parse_args()
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
