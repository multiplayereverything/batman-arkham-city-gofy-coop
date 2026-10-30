"""How closely the animated puppet follows its player, sideways (XY) and in height, through the lab bridge.

    python -I lab/track_check.py [--seconds 15] [--max-p95 60]

Needs the game hosting with a bot circling on open floor (lab/botfriend.sh). The error is between the puppet and
the interpolated position it should be at (the same target 0.2's snapped puppets had), so the network delay
is not counted; the animation's own lag is.
"""
import argparse
import json
import socket
import time


def call(line):
    with socket.create_connection(("127.0.0.1", 37900), timeout=5) as s:
        s.sendall((line + "\n").encode())
        b = b""
        while b"\n" not in b:
            b += s.recv(1 << 16)
    return json.loads(b.split(b"\n")[0])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=15)
    ap.add_argument("--max-p95", type=float, default=60)
    a = ap.parse_args()
    xy, z, kin, refused = [], [], 0, 0
    end = time.time() + a.seconds
    while time.time() < end:
        pups = call("anim")["puppets"]
        if pups and pups[0]["err_xy"] >= 0:
            p = pups[0]
            xy.append(p["err_xy"])
            z.append(abs(p["err_z"]))
            kin += 1 if p["kinematic"] else 0
            refused = p["pulls_refused"]
        time.sleep(0.05)
    if not xy:
        print("FAIL track: no puppet")
        return
    xy.sort()
    z.sort()
    q = lambda v, f: v[min(len(v) - 1, int(len(v) * f))]
    ok = q(xy, 0.95) <= a.max_p95
    print(f"{'PASS' if ok else 'FAIL'} track: {len(xy)} samples, xy p50 {q(xy, .5):.0f} p95 {q(xy, .95):.0f} max {xy[-1]:.0f} uu; "
          f"|z| p50 {q(z, .5):.0f} p95 {q(z, .95):.0f}; kinematic {kin}; corrections refused {refused}")


if __name__ == "__main__":
    main()
