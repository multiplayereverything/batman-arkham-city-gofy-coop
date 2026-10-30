"""Start or stop game copies for interactive lab work (coop-anything gameslot lease, pid-scoped stop).

    python -I lab/launch.py start host [guest]     one or two copies; each enters the open world by itself
    python -I lab/launch.py stop                   close only the copies this tool started

Copy 0 ("host") has its lab bridge on 37900, copy 1 ("guest") on 37901. Both use direct transport on 37950;
the guest does not join until told to (python -I lab/bridge.py 37901 join direct 127.0.0.1:37950).
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY"
EXE = os.path.join(GAME, "Binaries", "Win32", "BatmanAC.exe")
STATE = os.path.join(ROOT, "local", "launch.json")
GAMESLOT = os.path.join(ROOT, "toolkits", "coop-anything", "tools", "gameslot", "gameslot.py")
OWNER = "batcoop-launch"
OUT = os.path.join(ROOT, "lab", "out", "session")


def display_keep():
    """lab/display_keep.py (the player's resolution through lab runs); -I leaves lab/ off sys.path."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("display_keep", os.path.join(ROOT, "lab", "display_keep.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def pids():
    p = subprocess.run(["tasklist", "/FI", "IMAGENAME eq BatmanAC.exe", "/FO", "CSV", "/NH"], capture_output=True, text=True)
    return {int(l.split('","')[1]) for l in p.stdout.splitlines() if l.lower().startswith('"batmanac')}


def gameslot(*args):
    env = dict(os.environ, GAMESLOT_DIR=os.path.join(ROOT, "local", "gameslot"))
    return subprocess.run([sys.executable, "-I", GAMESLOT, *args], env=env, capture_output=True, text=True)


def start(roles):
    if pids():
        sys.exit("Arkham City is already running; the lab only works with copies it starts")
    r = gameslot("acquire", "--owner", OWNER, "--slots", str(len(roles)), "--ttl", "7200", "--wait", "0")
    if r.returncode != 0:
        sys.exit("gameslot: " + (r.stdout + r.stderr).strip())
    os.makedirs(OUT, exist_ok=True)
    display_keep().save()  # lab copies run in a window; the game saves its window size when it quits
    started = []
    for i, role in enumerate(roles):
        before = pids()
        env = dict(os.environ, SteamAppId="200260", SteamGameId="200260",
                   BATCOOP_LABPORT=str(37900 + i), BATCOOP_LABENTERWORLD="1", BATCOOP_RUNINBACKGROUND="1",
                   BATCOOP_TRANSPORT="direct", BATCOOP_DIRECTPORT="37950", BATCOOP_LINKPORT=str(37940 + i),
                   BATCOOP_NAME="Lab" + role.capitalize(), BATCOOP_LOGTAG=role,
                   BATCOOP_PROBEPATH=os.path.join(OUT, f"{role}.probe.jsonl"), BATCOOP_PROBEMACHINE=role)
        res = "-ResX=960 -ResY=540".split()
        # LAB_URL: start straight into that map URL (works without the mod, e.g. to compare with BatCoop off).
        url = [os.environ["LAB_URL"]] if os.environ.get("LAB_URL") else []
        if url:
            env["BATCOOP_LABENTERWORLD"] = "0"
        subprocess.Popen([EXE, *url, "-windowed", *res, "-nostartupmovies"], cwd=os.path.dirname(EXE), env=env)
        end = time.time() + 60
        new = set()
        while time.time() < end and not new:
            new = pids() - before
            time.sleep(1)
        if not new:
            print(f"{role}: no new BatmanAC.exe appeared")
            continue
        pid = sorted(new)[0]
        gameslot("register-pid", "--owner", OWNER, "--pid", str(pid))
        started.append({"role": role, "pid": pid, "bridge": 37900 + i})
        print(f"{role}: pid {pid}, bridge 127.0.0.1:{37900 + i}")
        if i + 1 < len(roles):
            time.sleep(25)  # let the first copy finish loading before the second competes for disk and RAM
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    json.dump(started, open(STATE, "w"))


def stop():
    started = json.load(open(STATE)) if os.path.exists(STATE) else []
    running = pids()
    for s in started:
        if s["pid"] in running:
            subprocess.run(["taskkill", "/PID", str(s["pid"]), "/F"], capture_output=True)
            print(f"closed {s['role']} (pid {s['pid']})")
    time.sleep(2)
    if not pids():
        print("resolution restored:", display_keep().restore())
    gameslot("release", "--owner", OWNER)
    if os.path.exists(STATE):
        os.remove(STATE)


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in ("start", "stop"):
        sys.exit(__doc__)
    if sys.argv[1] == "start":
        start(sys.argv[2:] or ["host"])
    else:
        stop()
