"""0.4.1 in-game lab: a guest joins the host's game from the main menu, against ONE real Arkham City copy.

    python -I lab/ingame_041.py [--keep]

The real game starts in the main menu (LAB_MENU=1) and joins a bot host that reports it is playing Catwoman's
opening (the CW start URL, past its opening movie). Checked: the guest refuses to open a world of its own from the
menu, follows the host into CW by itself, shows the Batman bot as a puppet it can spawn there, plays the host's
cutscene when the host starts one, and gives its window its size back when something shrinks it to a sliver.
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ingame_sync as lab  # noqa: E402  (helpers: game, wait, check, start_bot, log)

ROOT = lab.ROOT
CW_URL = ("batentry?Area=CW,CW_C1?Chapters=1,1z,Z1?Start=CW_Start?NoFadeIn?NoFadeOut?Players=Playable_Catwoman?"
          "Flags=Batman_RiddlesDisabled")
STATE = os.path.join(ROOT, "local", "launch.json")
lab.OUT = os.path.join(ROOT, "lab", "out", "ingame_041")
MOD_LOG = os.path.join(r"F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY", "BmGame", "Mods", "BatCoop", "logs")

user32 = ctypes.WinDLL("user32", use_last_error=True)
EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def game_window(pid):
    found = []

    def cb(h, _):
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h) and user32.GetWindow(h, 4) == 0:  # GW_OWNER: a top-level window
            found.append(h)
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found[0] if found else None


def client_size(h):
    r = wt.RECT()
    user32.GetClientRect(h, ctypes.byref(r))
    return r.right - r.left, r.bottom - r.top


def mod_log_text():
    return open(os.path.join(MOD_LOG, "batcoop-host.log"), encoding="utf-8", errors="replace").read()


def game_pid():
    for e in json.load(open(STATE)):
        if e.get("role") == "host":
            return e["pid"]
    return None


def main():
    os.makedirs(lab.OUT, exist_ok=True)
    open(os.path.join(lab.OUT, "ingame_sync.log"), "w").close()
    started = False
    try:
        lab.game("ping")
    except OSError:
        subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "launch.py"), "start", "host"], check=True,
                       env=dict(os.environ, LAB_MENU="1"))
        started = True
    try:
        run()
    finally:
        if started and "--keep" not in sys.argv:
            subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "launch.py"), "stop"])
    ok = lab.results and all(r[1] for r in lab.results)
    lab.log(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(r[1] for r in lab.results)} passed, {sum(not r[1] for r in lab.results)} failed)")
    sys.exit(0 if ok else 1)


def run():
    lab.log("=== 0.4.1: the real game waits in the main menu, a bot hosts Catwoman's opening")
    menu = lab.wait(lambda: lab.game("state"), lambda s: s.get("map", "x").lower() in ("", "frontend"), 240, "the main menu")
    if not lab.check("M_in_menu", menu, f"game in the main menu: map '{menu and menu.get('map')}'"):
        return
    time.sleep(5)
    p, bot = lab.start_bot(38902, 38962, "BotHost", (0, 0, 0))
    try:
        bot("host 37952")
        lab.wait(lambda: bot("state"), lambda s: s["state"] == "Hosting", 15, "bot hosting")
        # The host is in the main menu first: the guest joins the room and stays where it is.
        bot("hostworld FrontEnd 0 0")
        lab.game("join direct 127.0.0.1:37952")
        js = lab.wait(lambda: lab.game("state"), lambda s: s.get("state") == "Joined", 30, "game joined")
        time.sleep(3)
        st = lab.game("state")
        lab.check("M_guest_waits_in_menu", js and st.get("map", "x").lower() in ("", "frontend"),
                  f"joined ({js and js.get('status')}) and still in the main menu while the host is: map '{st.get('map')}'")

        gates = {i: lab.game(f"menugate {i}") for i in ("MMI_NewGame", "MMI_Continue", "MMI_CatwomanEPs", "MMI_OptionsMenu")}
        lab.check("M_menu_blocks_own_game", all(gates[i]["blocked"] for i in ("MMI_NewGame", "MMI_Continue", "MMI_CatwomanEPs"))
                  and not gates["MMI_OptionsMenu"]["blocked"],
                  "as a guest: " + ", ".join(f"{k} {'refused' if v['blocked'] else 'allowed'}" for k, v in gates.items()))

        # The host starts Catwoman's opening and is past its movie: the guest must follow on its own.
        bot(f"hostworld CW 1 30 {CW_URL}")
        lab.log("host now plays CW (30 s in)")
        arrived = lab.wait(lambda: lab.game("state"), lambda s: lab.in_world(s) and s.get("map", "").upper().startswith("CW"), 240,
                           "the guest in CW")
        log = mod_log_text()
        follow = [l for l in log.splitlines() if "following host:" in l]
        lab.check("M_guest_follows_into_cw", arrived and follow and "Movie=" not in follow[-1],
                  f"from the menu into {arrived and arrived.get('map')} with '{follow[-1].split('following host: ')[-1] if follow else '?'}'")
        if not arrived:
            return
        time.sleep(8)

        pup = lab.wait(lambda: lab.game("state"), lambda s: s.get("puppets") and s["puppets"][0].get("valid"), 30, "a puppet in CW")
        lines = [l for l in mod_log_text().splitlines() if "puppet for player" in l or "puppet:" in l]
        lab.check("M_puppet_in_cw", pup, f"the Batman bot shows as {pup and pup['puppets'][0]['class']} in Catwoman's level; "
                  f"log: {lines[-1].split(' ', 1)[-1] if lines else '-'}")

        # Cutscenes: the host reports a cutscene and then its end. The guest's own copy starts from the same story (a
        # script never starts one: that crashed the game); here none plays, so nothing may be ended or started.
        ia = lab.game("interps all")
        cands = ia.get("skippable", []) + ia.get("other", [])
        lab.log(f"cutscenes here: {ia.get('count')} ({len(ia.get('skippable', []))} skippable): {cands[:6]}")
        target = cands[0] if cands else "CW_C1.TheWorld:PersistentLevel:Main_Sequence:SeqAct_Interp_0"
        bot(f"cine {target}|1")
        gi = lab.wait(lambda: lab.game("interps"), lambda s: s["applied_cine"] == f"{target}|1", 15, "the host's cutscene seen")
        bot("cine -")
        gi2 = lab.wait(lambda: lab.game("interps"), lambda s: s["applied_cine"] == "", 15, "the host's cutscene over")
        st = lab.game("state")
        cl = [l for l in mod_log_text().splitlines() if "cutscene sync:" in l]
        lab.check("M_cutscene_relay", gi and gi2 and st.get("pawn") and gi2["ended_with_host"] == 0,
                  f"host cutscene seen {bool(gi)}, its end seen {bool(gi2)}, nothing forced or ended here "
                  f"(ended {gi2 and gi2['ended_with_host']}), still playing; log: {cl[-1].split(' ', 1)[-1] if cl else '-'}")

        # The window: shrink it the way leaving fullscreen did, and see it put back.
        pid = game_pid()
        h = game_window(pid) if pid else None
        if h:
            w0 = client_size(h)
            user32.SetWindowPos(h, None, 100, 100, 160, 120, 0x0004 | 0x0010)  # SWP_NOZORDER | SWP_NOACTIVATE
            time.sleep(0.5)
            small = client_size(h)
            end = time.time() + 10
            while time.time() < end and client_size(h)[0] < 640:
                time.sleep(0.5)
            w1 = client_size(h)
            dl = [l for l in mod_log_text().splitlines() if "display: the game window shrank" in l]
            lab.check("M_window_size_restored", small[0] < 640 and w1[0] >= 640 and w1[1] >= 360,
                      f"window {w0[0]}x{w0[1]} shrunk to {small[0]}x{small[1]}, back to {w1[0]}x{w1[1]}; log: "
                      f"{dl[-1].split(' ', 1)[-1] if dl else '-'}")
        else:
            lab.check("M_window_size_restored", False, f"no game window for pid {pid}")

        hooks = lab.game("hooks").get("calls", {})
        lab.log("story hooks: " + json.dumps({k: v for k, v in hooks.items() if k[0].isupper() or "Map" in k or "Game_" in k}))
        st = lab.game("state")
        lab.check("M_still_in_room", st.get("state") == "Joined" and st.get("pawn"),
                  f"after all that: {st.get('state')}, in {st.get('map')}")
    finally:
        try:
            bot("quit")
        except OSError:
            pass
        p.wait(timeout=10)
        try:
            lab.game("leave")
        except OSError:
            pass


if __name__ == "__main__":
    main()
