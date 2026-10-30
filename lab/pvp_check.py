"""PVP check against one real game copy (the defender) and a bot (the attacker), through the lab bridge.

    python -I lab/pvp_check.py [--seconds 30] [--counter-every 2]

Needs the game hosting with PVP on and a bot that attacks it (lab/botfriend.sh ... --pvp-attack-every 6).
Samples the combat proxy against the bot's puppet (the counter icon is drawn over the proxy, so the two must be
in one place), arms the lab's perfect-timing counter for every Nth attack, and prints PASS/FAIL lines.
"""
import argparse
import json
import math
import socket
import time

PORT = 37900


def call(line):
    with socket.create_connection(("127.0.0.1", PORT), timeout=5) as s:
        s.settimeout(35)
        s.sendall((line + "\n").encode())
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(1 << 16)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.split(b"\n", 1)[0])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--counter-every", type=int, default=2)
    a = ap.parse_args()

    call("combat watch log clear")
    start = call("pvp")["pvp"]
    hp0 = call("insp pawn.Health 0")["value"]
    max_icon_dist, max_dist, samples, icon_samples = 0.0, 0.0, 0, 0
    forced_seen = start["forced"]
    attacks = 0
    end = time.time() + a.seconds
    half = time.time() + a.seconds / 2
    next_place = 0.0
    armed_once = False
    while time.time() < end:
        pv = call("pvp")["pvp"]
        st = call("state")
        if pv["proxy_list"] and st["puppets"]:
            d = math.dist(pv["proxy_list"][0]["pos"], st["puppets"][0]["pos"])
            samples += 1
            max_dist = max(max_dist, d)
            if pv["proxy_list"][0]["move"]:
                icon_samples += 1
                max_icon_dist = max(max_icon_dist, d)
        if pv["forced"] > forced_seen:
            forced_seen = pv["forced"]
            attacks += 1
        busy = bool(pv["proxy_list"] and pv["proxy_list"][0]["move"])
        if not busy and st["puppets"] and time.time() >= next_place:
            # A real attacker's strike carries them next to the defender; the bot stands still, so keep the
            # defender within a thug's reach between attacks (hits knock them back).
            next_place = time.time() + 1.0
            p = st["puppets"][0]["pos"]
            call(f"tp {p[0] + 120:.0f} {p[1]:.0f} {p[2] + 20:.0f}")
        # Perfect-timing counters for the second half of the run (the first half takes the hits).
        if a.counter_every > 0 and time.time() > half and not armed_once:
            call("combat counter")
            armed_once = True
        if armed_once and not busy and not call("combat")["armed"] and time.time() < end - 6:
            call("combat counter")
        time.sleep(0.15)
    pv = call("pvp")["pvp"]
    hp1 = call("insp pawn.Health 0")["value"]
    # A hit within 1.5 s after a successful counter press means the counter did not stop it.
    log = call("combat log")["log"]
    counter_hits, last_counter, last_hp = 0, None, None
    for l in log:
        t = float(l.split()[0])
        if "counter pressed" in l and l.endswith("True"):
            last_counter = t
        if "batman_hp=" in l:
            hp = int(l.split("batman_hp=")[1].split()[0])
            if last_hp is not None and hp < last_hp and last_counter is not None and t - last_counter < 1.5:
                counter_hits += 1
            last_hp = hp
    hits = pv["hits"] - start["hits"]
    counters = pv["counters"] - start["counters"]
    misses = pv["misses"] - start["misses"]
    forced = pv["forced"] - start["forced"]

    def check(name, ok, detail):
        print(("PASS " if ok else "FAIL ") + name + ": " + detail)
        return ok

    ok = all([
        check("proxy_pinned", samples > 0 and max_dist < 30, f"max proxy-puppet distance {max_dist:.0f} uu over {samples} samples ({icon_samples} during attacks, max {max_icon_dist:.0f})"),
        check("forced_attacks", forced >= 3, f"{forced} incoming attacks played as real thug attacks"),
        check("hits_land", hits >= 1, f"{hits} hits reported (health {hp0} -> {hp1}; a defeat restores full health)"),
        check("counter_blocks_hit", counters == 0 or counter_hits == 0, f"{counter_hits} hits landed right after a counter"),
        check("counters_work", a.counter_every <= 0 or counters >= 3, f"{counters} counters reported (every attack in the second half is countered)"),
        check("few_misses", misses <= max(1, forced // 3), f"{misses} misses of {forced} (an attack the game gave up on)"),
    ])
    for l in call("combat log")["log"][-12:]:
        print("   ", l)
    print("RESULT:", "PASS" if ok else "FAIL")


if __name__ == "__main__":
    main()
