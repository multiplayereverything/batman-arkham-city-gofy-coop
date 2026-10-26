"""Offline lab in one command: build, run the bot suite against the packaged (or dev) link, judge the lag probe
streams with coop-anything animcheck. Prints RESULT: PASS or FAIL.

    python -I lab/run_offline.py [--dev]     --dev uses the dev build of BatCoopLink instead of dist/
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")
OUT = os.path.join(ROOT, "lab", "out", "offline")


def main():
    dev = "--dev" in sys.argv
    if subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "build.py"), "check", "link", "offline"]).returncode:
        sys.exit("RESULT: FAIL (build)")
    if dev:
        link = os.path.join(ROOT, "link", "BatCoopLink", "bin", "Release", "net10.0", "win-x64", "BatCoopLink.exe")
    else:
        subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "deploy.py"), "package"], check=True)
        link = os.path.join(ROOT, "dist", "BatCoop", "link", "BatCoopLink.exe")
    suite = subprocess.run([DOTNET, os.path.join(ROOT, "lab", "Offline", "bin", "Release", "net10.0", "Offline.dll"), link, OUT])
    anim = subprocess.run([sys.executable, "-I", os.path.join(ROOT, "toolkits", "coop-anything", "tools", "animcheck", "cli.py"),
                           "analyze", "host=" + os.path.join(OUT, "lag.host.probe.jsonl"),
                           "guest=" + os.path.join(OUT, "lag.guest.probe.jsonl"),
                           "--profile", os.path.join(ROOT, "lab", "animcheck_profile.json"),
                           "--avatar", "host_player=host:0x100>guest:0x200", "--avatar", "guest_player=guest:0x101>host:0x201",
                           "--delay-ms", "100", "--latency-ms", "150",
                           "--assert", "avatar.pos_err_p95<=40", "--assert", "avatar.facing_err_p95_deg<=10", "--assert", "glitches==0",
                           "--html", os.path.join(OUT, "lag.animcheck.html"), "--json", os.path.join(OUT, "lag.animcheck.json")])
    # Mutation twins: each protection switched off must make its own test fail (coop-anything tools/mutants).
    twins = {"guest_local_write": "world_story_sync", "no_epoch": "npc_sync", "skip_twice": "cutscene_vote", "no_digest": "world_story_sync"}
    caught = []
    for mut, test in twins.items():
        env = dict(os.environ, BATCOOP_MUT=mut)
        r = subprocess.run([DOTNET, os.path.join(ROOT, "lab", "Offline", "bin", "Release", "net10.0", "Offline.dll"), link,
                            os.path.join(OUT, "twin-" + mut), "--only", test], env=env, capture_output=True, text=True)
        failed = [l for l in r.stdout.splitlines() if l.startswith("FAIL")]
        ok_twin = r.returncode != 0 and failed
        caught.append(ok_twin)
        print(f"{'PASS' if ok_twin else 'FAIL'} twin {mut}: {'caught by ' + failed[0][5:90] if failed else 'NOT caught: the test passes without this protection'}")
    ok = suite.returncode == 0 and anim.returncode == 0 and all(caught)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} (suite {'ok' if suite.returncode == 0 else 'failed'}, animcheck {'ok' if anim.returncode == 0 else 'failed'}, twins caught {sum(caught)}/{len(caught)})")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
