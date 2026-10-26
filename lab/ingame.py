"""In-game lab: one real Arkham City copy hosts, a bot player joins it, both move, and the result is judged from
the mod's lab bridge, both sides' probe streams (coop-anything animcheck) and the game's own screenshot.

    python -I lab/ingame.py [--keep] [--steps a,b] [--seconds 15]

Nothing is typed or clicked: the mod enters the open world by itself on the temporary save slot, and the lab
drives it through the bridge on 127.0.0.1. Settings travel as BATCOOP_* environment variables, so the player's
BatCoop.ini is not touched. Only the game process this script started is ever closed.
"""
import json
import os
import socket
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY"
EXE = os.path.join(GAME, "Binaries", "Win32", "BatmanAC.exe")
BMSDK_LOG = os.path.join(GAME, "Binaries", "Win32", "BmSDK.log")
MOD_LOG = os.path.join(GAME, "BmGame", "Mods", "BatCoop", "logs", "batcoop.log")
OUT = os.path.join(ROOT, "lab", "out", "ingame")
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")
OFFLINE = os.path.join(ROOT, "lab", "Offline", "bin", "Release", "net10.0", "Offline.dll")
LINK = os.path.join(ROOT, "dist", "BatCoop", "link", "BatCoopLink.exe")
GAMESLOT = os.path.join(ROOT, "toolkits", "coop-anything", "tools", "gameslot", "gameslot.py")
ANIMCHECK = os.path.join(ROOT, "toolkits", "coop-anything", "tools", "animcheck", "cli.py")

LAB_PORT = 37900
LINK_PORT = 37940
DIRECT_PORT = 37950
OWNER = "batcoop-ingame"

results = []


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    with open(os.path.join(OUT, "ingame.log"), "a", encoding="utf-8") as f:
        f.write(line + "\n")


def check(name, ok, detail):
    results.append((name, bool(ok), detail))
    log(f"{'PASS' if ok else 'FAIL'} {name}: {detail}")
    return ok


def game_pids():
    p = subprocess.run(["tasklist", "/FI", "IMAGENAME eq BatmanAC.exe", "/FO", "CSV", "/NH"], capture_output=True, text=True)
    pids = set()
    for line in p.stdout.splitlines():
        parts = [x.strip('"') for x in line.split('","')]
        if len(parts) > 1 and parts[0].lower().startswith("batmanac"):
            pids.add(int(parts[1]))
    return pids


def launcher_started(after):
    p = subprocess.run(["powershell", "-NoProfile", "-Command",
                        f"Get-Process BmLauncher -ErrorAction SilentlyContinue | Where-Object {{ $_.StartTime -gt (Get-Date '{time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(after))}') }} | ForEach-Object {{ $_.Id }}"],
                       capture_output=True, text=True)
    return [int(x) for x in p.stdout.split()]


def close_launcher(after):
    for pid in launcher_started(after):
        subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)
        log(f"closed BmLauncher {pid} (started by this run)")


def steam_running():
    p = subprocess.run(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/NH"], capture_output=True, text=True)
    return "steam.exe" in p.stdout.lower()


class Bridge:
    def __init__(self, port):
        self.port = port
        self.sock = None
        self.buf = b""

    def connect(self, timeout):
        end = time.time() + timeout
        while time.time() < end:
            try:
                self.sock = socket.create_connection(("127.0.0.1", self.port), timeout=2)
                self.sock.settimeout(35)
                return True
            except OSError:
                time.sleep(1)
        return False

    def cmd(self, line):
        self.sock.sendall((line + "\n").encode())
        while b"\n" not in self.buf:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("bridge closed")
            self.buf += chunk
        raw, self.buf = self.buf.split(b"\n", 1)
        return json.loads(raw.decode())

    def wait(self, pred, timeout, every=1.0, what=""):
        end = time.time() + timeout
        last = None
        while time.time() < end:
            try:
                last = self.cmd("state")
                if pred(last):
                    return last
            except (OSError, ValueError, ConnectionError) as e:
                last = {"error": str(e)}
            time.sleep(every)
        log(f"timed out waiting for {what}; last state: {json.dumps(last)[:600]}")
        return None


def owner_jumps(path, ref, threshold=300.0):
    """(rx_ms start, rx_ms end, distance) for every sample-to-sample move above threshold of actor ref."""
    out, prev = [], None
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                j = json.loads(line)
            except ValueError:
                continue
            a = [x for x in j.get("actors", []) if x.get("ref") == ref]
            if not a:
                continue
            pos = a[0]["pos"]
            if prev is not None:
                d = sum((p - q) ** 2 for p, q in zip(pos, prev[1])) ** 0.5
                if d > threshold:
                    out.append((prev[0], j["rx_ms"], d))
            prev = (j["rx_ms"], pos)
    return out


def gameslot(*args):
    env = dict(os.environ, GAMESLOT_DIR=os.path.join(ROOT, "local", "gameslot"))
    return subprocess.run([sys.executable, "-I", GAMESLOT, *args], env=env, capture_output=True, text=True)


def newest_screenshot(after):
    d = os.path.join(os.environ["USERPROFILE"], "Documents", "WB Games", "Batman Arkham City GOTY", "BmGame", "ScreenShots")
    if not os.path.isdir(d):
        return None
    shots = [os.path.join(d, f) for f in os.listdir(d)]
    shots = [s for s in shots if os.path.getmtime(s) >= after]
    return max(shots, key=os.path.getmtime) if shots else None


def main():
    keep = "--keep" in sys.argv
    seconds = 15
    if "--seconds" in sys.argv:
        seconds = float(sys.argv[sys.argv.index("--seconds") + 1])
    os.makedirs(OUT, exist_ok=True)
    open(os.path.join(OUT, "ingame.log"), "w").close()

    if not steam_running():
        sys.exit("Steam is not running; the game needs it")
    before = game_pids()
    if before:
        sys.exit(f"Arkham City is already running (pids {sorted(before)}); the lab never touches a game it did not start")
    r = gameslot("acquire", "--owner", OWNER, "--slots", "1", "--ttl", "1800", "--wait", "0")
    if r.returncode != 0:
        sys.exit("gameslot: " + (r.stdout + r.stderr).strip())

    env = dict(os.environ)
    env.update({
        # Started outside Steam, the exe's Steam wrapper relaunches the game through Steam, whose default target
        # is BmLauncher (a window waiting for a click). Steam's own launch environment avoids that; Steam still
        # checks that this account owns the game.
        "SteamAppId": "200260",
        "SteamGameId": "200260",
        "BATCOOP_LABPORT": str(LAB_PORT),
        "BATCOOP_LABENTERWORLD": "1",
        "BATCOOP_RUNINBACKGROUND": "1",
        "BATCOOP_TRANSPORT": "direct",
        "BATCOOP_DIRECTPORT": str(DIRECT_PORT),
        "BATCOOP_LINKPORT": str(LINK_PORT),
        "BATCOOP_NAME": "LabHost",
        "BATCOOP_PROBEPATH": os.path.join(OUT, "game.probe.jsonl"),
        "BATCOOP_PROBEMACHINE": "game",
    })
    t_launch = time.time()
    log("launching Arkham City (windowed 1280x720)")
    subprocess.Popen([EXE, "-windowed", "-ResX=1280", "-ResY=720", "-nostartupmovies"], cwd=os.path.dirname(EXE), env=env)

    pids = set()
    bridge = Bridge(LAB_PORT)
    bot = None
    try:
        # The Steam wrapper may restart the exe, so track every BatmanAC.exe that appears after launch, and fail
        # early if the game hands over to BmLauncher instead of starting.
        end = time.time() + 60
        while time.time() < end:
            pids |= game_pids() - before
            alive = game_pids() & pids
            if launcher_started(t_launch):
                check("game_started", False, "the game opened BmLauncher instead of starting (closed it)")
                close_launcher(t_launch)
                return
            if alive and time.time() - t_launch > 15:
                break
            time.sleep(1)
        pids = game_pids() & pids
        if not check("game_started", pids, f"BatmanAC.exe pids {sorted(pids)}"):
            return
        for pid in pids:
            gameslot("register-pid", "--owner", OWNER, "--pid", str(pid))

        if not check("bridge_up", bridge.connect(240), "BmSDK compiled BatCoop and the lab bridge answered"):
            return
        st = bridge.wait(lambda s: s.get("pawn") and s.get("map", "").lower() not in ("", "frontend"), 300, 2, "the open world")
        if not check("entered_world", st, f"map {st and st.get('map')}, pawn {st and st.get('pawn')} at {st and st.get('pos')}"):
            return
        time.sleep(5)  # let streaming settle

        bridge.cmd("host direct")
        st = bridge.wait(lambda s: s.get("state") == "Hosting", 30, 0.5, "hosting")
        if not check("game_hosts", st, f"status '{st and st.get('status')}' (link started by the mod)"):
            return
        pos = st["pos"]

        bot_out = open(os.path.join(OUT, "bot.out.txt"), "w")
        bot = subprocess.Popen([DOTNET, OFFLINE, "bot", "--link-exe", LINK, "--join", f"127.0.0.1:{DIRECT_PORT}",
                                "--center", f"{pos[0] + 300},{pos[1]},{pos[2]}", "--radius", "250", "--speed", "250",
                                "--seconds", str(seconds + 45), "--probe", os.path.join(OUT, "bot.probe.jsonl"),
                                "--out", OUT, "--name", "LabBot"], stdout=bot_out, stderr=subprocess.STDOUT)

        st = bridge.wait(lambda s: s.get("puppets") and s["puppets"][0].get("valid"), 40, 0.5, "the bot's puppet")
        had_puppet = bool(st)
        check("puppet_spawned", st, f"puppet {st and st['puppets'][0]}" if st else "no puppet")
        if st:
            st = bridge.wait(lambda s: s.get("remotes") and s["remotes"][0].get("states", 0) > 40, 10, 0.5, "bot traffic") or st
            rm = st["remotes"][0]
            check("bot_seen", rm.get("name") == "LabBot" and rm.get("states", 0) > 10,
                  f"remote '{rm.get('name')}' states={rm.get('states')} rtt={rm.get('rtt_ms')}")

        t_shot = time.time()
        bridge.cmd("exec shot")
        time.sleep(3)
        shot = newest_screenshot(t_shot - 1)
        check("screenshot", shot, f"{shot}")

        # Both move now: the bot keeps circling, the game's own player walks a circle too.
        bridge.cmd(f"walk 200 {seconds}")
        samples = []
        t_end = time.time() + seconds
        while time.time() < t_end:
            s = bridge.cmd("state")
            if s.get("puppets"):
                p = s["puppets"][0]
                samples.append((time.time(), p.get("pos"), p.get("yaw"), p.get("physics")))
            time.sleep(0.25)
        moved = [s for s in samples if s[1]]
        span = 0
        if len(moved) > 2:
            xs = [m[1][0] for m in moved]; ys = [m[1][1] for m in moved]
            span = max(max(xs) - min(xs), max(ys) - min(ys))
        check("puppet_moves", span > 300, f"puppet travelled a {span:.0f} uu wide path in {seconds:.0f} s ({len(moved)} samples)")

        t_shot = time.time()
        bridge.cmd("exec shot")
        time.sleep(3)
        shot2 = newest_screenshot(t_shot - 1)

        bot.wait(timeout=120)
        bot_out.close()
        summary = [l for l in open(os.path.join(OUT, "bot.out.txt"), encoding="utf-8", errors="replace") if l.startswith("BOT_SUMMARY")]
        if check("bot_summary", summary, summary[0].strip() if summary else "bot printed no summary"):
            b = json.loads(summary[0].split(" ", 1)[1])
            check("game_to_bot", b["host_states"] > 100 and b["host_name"] == "LabHost",
                  f"bot received {b['host_states']} states from '{b['host_name']}' ({b['host_char']}, world {b['host_world']}), rtt {b['rtt_ms']} ms")

        if had_puppet:
            st = bridge.wait(lambda s: not s.get("puppets"), 15, 0.5, "puppet removal after the bot left")
            check("puppet_removed_on_leave", st, "puppet destroyed when the bot left" if st else "puppet still there")
        else:
            check("puppet_removed_on_leave", False, "not tested: no puppet ever existed")

        # A jump in a player's OWN stream is ground truth (the lab's scripted walk can be refused by level geometry
        # for a few frames); the observer is expected to copy it. Exclude those windows from both views and say so.
        # A jump that only the observer shows is still a failure.
        suppress = []
        for path, ref in ((os.path.join(OUT, "game.probe.jsonl"), "0x100"), (os.path.join(OUT, "bot.probe.jsonl"), "0x101")):
            for t0, t1, dist in owner_jumps(path, ref):
                log(f"owner {ref} itself moved {dist:.0f} uu in {t1 - t0:.0f} ms at rx {t0:.0f}: excluded from animcheck")
                suppress += ["--suppress", f"rubberband:*:{t0 - 300:.0f}-{t1 + 600:.0f}"]
        p = subprocess.run([sys.executable, "-I", ANIMCHECK, "analyze", *suppress,
                            "game=" + os.path.join(OUT, "game.probe.jsonl"), "bot=" + os.path.join(OUT, "bot.probe.jsonl"),
                            "--profile", os.path.join(ROOT, "lab", "animcheck_profile.json"),
                            "--avatar", "host=game:0x100>bot:0x200", "--avatar", "bot=bot:0x101>game:0x201",
                            "--delay-ms", "100", "--latency-ms", "150", "--auto-offset",
                            "--html", os.path.join(OUT, "animcheck.html"), "--json", os.path.join(OUT, "animcheck.json")],
                           capture_output=True, text=True)
        log(p.stdout.strip()[-1500:])
        check("animcheck", p.returncode == 0, "probe streams judged by coop-anything animcheck (see animcheck.html)")
        log(f"screenshots: {shot} {shot2}")
    finally:
        if bot and bot.poll() is None:
            bot.kill()
        if not keep and pids:
            try:
                bridge.cmd("exec exit")
            except Exception:
                pass
            end = time.time() + 30
            while time.time() < end and (game_pids() & pids):
                time.sleep(1)
            for pid in game_pids() & pids:
                log(f"game {pid} still running; ending it by pid")
                subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
        gameslot("release", "--owner", OWNER)

    ok = all(r[1] for r in results)
    log(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(r[1] for r in results)} passed, {sum(not r[1] for r in results)} failed)")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
