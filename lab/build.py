"""Build steps for the lab: compile check against BmSDK, the link helper and the offline tests.

    python -I lab/build.py check            compile the mod like BmSDK does at game start
    python -I lab/build.py link             build BatCoopLink.exe (Release, win-x64)
    python -I lab/build.py offline          build the offline lab
    python -I lab/build.py all

Prints one line per distinct error or warning and exits non-zero on any error.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")

PROJECTS = {
    "check": (os.path.join(ROOT, "lab", "CompileCheck", "CompileCheck.csproj"), "Debug"),
    "link": (os.path.join(ROOT, "link", "BatCoopLink", "BatCoopLink.csproj"), "Release"),
    "offline": (os.path.join(ROOT, "lab", "Offline", "Offline.csproj"), "Release"),
}


def build(name, extra=()):
    proj, config = PROJECTS[name]
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    cmd = [DOTNET, "build", proj, "-c", config, "-nologo", "-clp:NoSummary", *extra]
    p = subprocess.run(cmd, cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    seen = []
    for line in p.stdout.splitlines():
        if not re.search(r"\b(error|warning) [A-Z]+\d+", line):
            continue
        line = re.sub(r"\s*\[[^\]]+\.csproj\]$", "", line.strip())
        line = line.replace(ROOT + os.sep, "")
        if line not in seen:
            seen.append(line)
    for line in seen:
        print(line)
    ok = p.returncode == 0
    print(f"{name}: {'OK' if ok else 'FAILED'} ({sum(' error ' in l for l in seen)} errors, {sum(' warning ' in l for l in seen)} warnings)")
    return ok


def main():
    targets = sys.argv[1:] or ["all"]
    if targets == ["all"]:
        targets = ["check", "link", "offline"]
    ok = all([build(t) for t in targets])
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
