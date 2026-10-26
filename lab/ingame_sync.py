"""In-game sync lab: world, story, mission, NPC, pause and cutscene-vote sync against ONE real Arkham City copy.

    python -I lab/ingame_sync.py [--keep] [--skip-travel]

Run A: the real game hosts and a bot guest joins. Run B: a bot hosts (replaying NPCs recorded from the real game in
run A) and the real game joins as the guest. Both sides are checked through their command ports after every step.
One PC cannot run two copies here (not enough free memory), so the bot stands in for the second player.
The game copy is started with lab/launch.py if none is running, and closed at the end unless --keep.
"""
import json
import os
import socket
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "lab", "out", "ingame_sync")
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")
OFFLINE = os.path.join(ROOT, "lab", "Offline", "bin", "Release", "net10.0", "Offline.dll")
LINK = os.path.join(ROOT, "dist", "BatCoop", "link", "BatCoopLink.exe")
GAME_PORT = 37900
results = []


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    with open(os.path.join(OUT, "ingame_sync.log"), "a", encoding="utf-8") as f:
        f.write(line + "\n")


def check(name, ok, detail):
    results.append((name, bool(ok)))
    log(f"{'PASS' if ok else 'FAIL'} {name}: {detail}")
    return bool(ok)


def call(port, line, timeout=35):
    with socket.create_connection(("127.0.0.1", port), timeout=5) as s:
        s.settimeout(timeout)
        s.sendall((line + "\n").encode())
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(1 << 20)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.split(b"\n", 1)[0].decode())


def game(line):
    return call(GAME_PORT, line)


def wait(fn, pred, timeout, what):
    end = time.time() + timeout
    last = None
    while time.time() < end:
        try:
            last = fn()
            if pred(last):
                return last
        except (OSError, ValueError, KeyError, TypeError) as e:
            last = {"error": str(e)}
        time.sleep(0.5)
    log(f"timed out waiting for {what}: {json.dumps(last)[:500]}")
    return None


def start_bot(port, link_port, name, pos):
    out = open(os.path.join(OUT, f"{name}.out.txt"), "w")
    p = subprocess.Popen([DOTNET, OFFLINE, "botserve", "--link-exe", LINK, "--port", str(port), "--link-port", str(link_port),
                          "--name", name, "--x", str(pos[0] + 300), "--y", str(pos[1]), "--z", str(pos[2])],
                         stdout=out, stderr=subprocess.STDOUT, cwd=OUT)
    bot = lambda line: call(port, line)
    if not wait(lambda: bot("state"), lambda s: s.get("ok"), 20, f"{name} command port"):
        raise RuntimeError(f"{name} did not start")
    return p, bot


def ensure_game():
    try:
        game("ping")
        return False
    except OSError:
        pass
    subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "launch.py"), "start", "host"], check=True)
    return True


def in_world(s):
    return s.get("pawn") and s.get("map", "").lower() not in ("", "frontend")


def run_a():
    log("=== run A: the real game hosts, a bot joins as the guest")
    game("leave")
    time.sleep(1)
    game("host direct")
    st = wait(lambda: game("state"), lambda s: s.get("state") == "Hosting", 30, "game hosting")
    if not check("A_game_hosts", st, f"{st and st.get('status')}"):
        return None
    pos = st["pos"]
    p, bot = start_bot(38900, 38960, "BotGuest", pos)
    try:
        bot("join 127.0.0.1:37950")
        bs = wait(lambda: bot("state"), lambda s: s["state"] == "Joined" and s["world"]["baseline"], 30, "bot baseline")
        gf = game("flags")
        gw = game("world")
        gc = game("chapter")
        check("A_world_snapshot", bs and len(bs["flags"]) == gf["set"] and bs["ints"].get("ch:main") == gc["chapter"]
              and bs["map"] == gw["map"],
              f"bot got {bs and len(bs['flags'])} flags (game has {gf['set']} set of {gf['catalog']} known), chapter "
              f"{bs and bs['ints'].get('ch:main')} (game {gc['chapter']}), map {bs and bs['map']} (game {gw['map']})")

        game("setflag BatCoopLab_HostFlag 1")
        game(f"chapter {gc['chapter'] + 3}")
        bs = wait(lambda: bot("state"), lambda s: "BatCoopLab_HostFlag" in s["flags"] and s["ints"].get("ch:main") == gc["chapter"] + 3,
                  10, "host change at the bot")
        check("A_host_story_to_guest", bs, f"a story flag and chapter {gc['chapter'] + 3} set in the real game reached the guest "
              f"(seq {bs and bs['world']['seq']})")
        game(f"chapter {gc['chapter']}")

        bot("setflag BatCoopLab_GuestStartsMission 1")
        gv = wait(lambda: game("getflag BatCoopLab_GuestStartsMission"), lambda s: s.get("value") is True, 10, "guest intent in game")
        bs = wait(lambda: bot("state"), lambda s: "BatCoopLab_GuestStartsMission" in s["flags"], 10, "intent echoed back")
        gw = game("world")
        check("A_guest_intent_to_host", gv and bs and gw["intents_applied"] >= 1,
              f"a mission flag the guest set was applied by the real host ({gw['intents_applied']} intent(s)) and came back as host state")

        bs = wait(lambda: bot("state"), lambda s: len(s["npc_mirrors"]) > 0 and all(m["States"] > 10 for m in s["npc_mirrors"]), 15, "NPC mirrors")
        gn = game("npcs")
        check("A_npcs_to_guest", bs and len(bs["npc_mirrors"]) == gn["shared"],
              f"guest mirrors {bs and len(bs['npc_mirrors'])} of the host's NPCs (host shares {gn['shared']} of {gn['live']} live), "
              f"e.g. {bs and bs['npc_mirrors'][0]['Key']}")

        target = None
        if bs and bs["npc_mirrors"]:
            target = max(bs["npc_mirrors"], key=lambda m: m["health"])
            before = target["health"]
            bot(f"hit {target['Key'].split(':')[-1]} 3")
            after = wait(lambda: bot("state"), lambda s: any(m["Key"] == target["Key"] and m["health"] < before for m in s["npc_mirrors"]),
                         10, "hit result")
            gn = game("npcs")
            check("A_guest_hit_on_host_npc", after and gn["hits_applied"] >= 1,
                  f"the guest's hit on {target['Key'].split(':')[-1]} was applied by the real host ({gn['hits_applied']} hit(s)); "
                  f"health {before} -> {after and [m['health'] for m in after['npc_mirrors'] if m['Key'] == target['Key']][0]}")

        skips0 = game("vote")["skips_done"]
        game("watch on")
        bot("watch on")
        bot("vote")
        gv = wait(lambda: game("vote"), lambda s: s["votes"] == 1 and s["needed"] == 2, 8, "1/2 votes")
        waited = gv and gv["skips_done"] == skips0
        game("skip")
        gv2 = wait(lambda: game("vote"), lambda s: s["skips_done"] == skips0 + 1, 8, "skip in game")
        bs = wait(lambda: bot("state"), lambda s: s["vote"]["skips"] == 1, 8, "skip at bot")
        check("A_cutscene_vote", waited and gv2 and bs, "one vote of two waited; the host's vote skipped on both machines once")
        game("watch auto")

        g1 = game("paused")
        game("pause")
        time.sleep(2)
        g2 = game("paused")
        check("A_no_pause", not g2["paused"] and g2["time"] > g1["time"] + 1.5,
              f"after a pause command the world still runs: game time {g1['time']:.1f} -> {g2['time']:.1f} in 2 s "
              f"(pauses refused so far: {g2['blocked_total']})")

        dump = game("npcdump 14")["npcs"]
        path = os.path.join(OUT, "npcs.json")
        json.dump(dump, open(path, "w"), indent=1)
        log(f"recorded {len(dump)} NPCs from the real game for run B")
        return path
    finally:
        try:
            bot("quit")
        except OSError:
            pass
        p.wait(timeout=10)
        game("leave")
        time.sleep(2)


def run_b(npcs_path, skip_travel):
    log("=== run B: a bot hosts (replaying the recorded NPCs), the real game joins as the guest")
    st = game("state")
    pos = st["pos"]
    p, bot = start_bot(38901, 38961, "BotHost", pos)
    try:
        bot(f"npcs_load {npcs_path}")
        bot("host 37951")
        wait(lambda: bot("state"), lambda s: s["state"] == "Hosting", 15, "bot hosting")
        bot("setflag BatCoopLab_FromBotHost 1")
        bot("chapter 7")
        time.sleep(1)
        game("join direct 127.0.0.1:37951")
        gw = wait(lambda: game("world"), lambda s: s.get("baseline") and s["seq"] >= 1, 30, "game baseline")
        gf = game("getflag BatCoopLab_FromBotHost")
        gc = game("chapter")
        check("B_host_story_applied_in_game", gw and gf["value"] is True and gc["chapter"] == 7,
              f"the real guest applied the host's flag ({gf['value']}) and chapter ({gc['chapter']}); {gw and gw['snapshots']} snapshot(s)")

        bot("setflag BatCoopLab_LaterHostFlag 1")
        gf = wait(lambda: game("getflag BatCoopLab_LaterHostFlag"), lambda s: s.get("value") is True, 10, "later txn in game")
        check("B_host_txn_applied_in_game", gf, "a later host transaction reached the real guest")

        game("setflag BatCoopLab_GuestNativeProgress 1")
        bs = wait(lambda: bot("state"), lambda s: "BatCoopLab_GuestNativeProgress" in s["flags"], 10, "guest progress at host")
        check("B_guest_progress_to_host", bs, "a flag the real guest's game set by itself reached the host as an intent")

        gn = wait(lambda: game("npcs"), lambda s: s["mirrors"] > 0 and s["bound"] == s["mirrors"], 20, "NPCs bound in game")
        if gn:
            close = [m for m in gn["mirror_list"] if m["local_pos"] and
                     sum((a - b) ** 2 for a, b in zip(m["local_pos"], m["host_pos"])) ** 0.5 < 150]
            check("B_npcs_bound_in_game", gn["bound"] == gn["mirrors"] and len(close) == len(gn["mirror_list"]),
                  f"{gn['bound']} host NPCs bound to the real game's own level NPCs ({sum(not m['spawned'] for m in gn['mirror_list'])} "
                  f"taken over, {sum(m['spawned'] for m in gn['mirror_list'])} spawned), all within 150 uu of the host's position; "
                  f"{gn['suppressed']} other local AI pawns frozen")
            time.sleep(2)
            gn2 = game("npcs")
            moved = [a for a in gn2["mirror_list"] for b in gn["mirror_list"] if a["id"] == b["id"] and a["local_pos"] and b["local_pos"]
                     and sum((x - y) ** 2 for x, y in zip(a["local_pos"], b["local_pos"])) ** 0.5 > 30]
            check("B_npcs_move_with_host", len(moved) > 0, f"{len(moved)} of {len(gn2['mirror_list'])} game NPCs moved with the host's NPCs in 2 s")
        else:
            check("B_npcs_bound_in_game", False, "no NPC was bound")

        gh = game("hit 4")
        hits = wait(lambda: bot("hits"), lambda s: len(s["hits"]) > 0, 10, "hit at bot host")
        check("B_game_hit_forwarded", gh.get("forwarded_to_host") and hits,
              f"the real guest's hit on {gh.get('target', '?').split(':')[-1]} went to the host instead of applying locally: {hits and hits['hits']}")

        skips0 = game("vote")["skips_done"]
        game("watch on")
        bot("watch on")
        game("skip")
        gv = wait(lambda: game("vote"), lambda s: s["votes"] == 1 and s["needed"] == 2 and s["skips_done"] == skips0, 8, "game's vote counted")
        bot("vote")
        gv2 = wait(lambda: game("vote"), lambda s: s["skips_done"] == skips0 + 1, 8, "skip in game")
        check("B_cutscene_vote_as_guest", gv and gv2, "the real guest's skip press counted as a vote (1/2); the host's vote skipped it in game")
        game("watch auto")

        g1 = game("paused")
        game("pause")
        time.sleep(2)
        g2 = game("paused")
        check("B_no_pause_as_guest", not g2["paused"] and g2["time"] > g1["time"] + 1.5,
              f"after a pause command the guest's world still runs: {g1['time']:.1f} -> {g2['time']:.1f} (refused so far: {g2['blocked_total']})")

        if not skip_travel:
            # Same mission, same map: the host reports another map with its URL; the real guest must follow it.
            url = game("world")["url"]
            bot(f"settext lvl:url {url}")
            bot("settext lvl:map OW_LabFollow")
            log("travel test: host now reports map OW_LabFollow with the game's own URL")
            left = wait(lambda: game("state"), lambda s: not s.get("pawn"), 40, "guest leaving the world to follow")
            bot("settext lvl:map OW")  # stop following once it has started
            back = wait(lambda: game("state"), lambda s: in_world(s) and s.get("state") == "Joined", 180, "guest back in the world")
            gw = wait(lambda: game("world"), lambda s: s.get("baseline"), 30, "baseline after travel") if back else None
            gf = wait(lambda: game("getflag BatCoopLab_FromBotHost"), lambda s: s.get("value") is True, 15, "story after load") if back else None
            gf = gf or {}
            check("B_guest_follows_host_map", left and back and gw and gf.get("value") is True,
                  f"the real guest followed the host's map ({'left' if left else 'did not leave'} the world, "
                  f"{'came back in ' + back['map'] if back else 'did not come back'}), still in the co-op game, and the shared story "
                  f"was restored after loading (flag {gf.get('value')})")
        return True
    finally:
        try:
            bot("quit")
        except OSError:
            pass
        p.wait(timeout=10)
        try:
            game("leave")
        except OSError:
            pass


def main():
    os.makedirs(OUT, exist_ok=True)
    open(os.path.join(OUT, "ingame_sync.log"), "w").close()
    started = ensure_game()
    if not wait(lambda: game("state"), in_world, 300, "the open world"):
        sys.exit("game did not reach the open world")
    time.sleep(3)
    try:
        npcs = run_a()
        if npcs:
            run_b(npcs, "--skip-travel" in sys.argv)
    finally:
        if started and "--keep" not in sys.argv:
            subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "launch.py"), "stop"])
    ok = results and all(r[1] for r in results)
    log(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(r[1] for r in results)} passed, {sum(not r[1] for r in results)} failed)")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
