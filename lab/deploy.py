"""Package BatCoop into dist/BatCoop and install it into the game's BmGame/Mods/BatCoop.

    python -I lab/deploy.py package                 build dist/BatCoop (mod scripts + self-contained BatCoopLink.exe)
    python -I lab/deploy.py install [--game DIR]    package, then copy into the game (keeps BatCoop.ini and logs/)

Only files from dist/BatCoop are written; the list goes to install/batcoop-installed-files.txt. Files that did not
change are not rewritten, so a running game only hot-reloads scripts that really changed.
"""
import hashlib
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")
DIST = os.path.join(ROOT, "dist", "BatCoop")
DEFAULT_GAME = r"F:\SteamLibrary\steamapps\common\Batman Arkham City GOTY"
KEEP = {"BatCoop.ini", "logs"}


def package():
    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DIST)
    src = os.path.join(ROOT, "mod", "BatCoop")
    shutil.copy2(os.path.join(src, "mod.toml"), DIST)
    shutil.copytree(os.path.join(src, "scripts"), os.path.join(DIST, "scripts"),
                    ignore=shutil.ignore_patterns("bin", "obj"))
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    cmd = [DOTNET, "publish", os.path.join(ROOT, "link", "BatCoopLink", "BatCoopLink.csproj"), "-c", "Release",
           "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
           "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", os.path.join(DIST, "link"), "-nologo"]
    p = subprocess.run(cmd, env=env, capture_output=True, text=True)
    if p.returncode != 0:
        print(p.stdout[-3000:])
        sys.exit("link publish failed")
    for extra in ("README.md",):
        if os.path.exists(os.path.join(ROOT, extra)):
            shutil.copy2(os.path.join(ROOT, extra), DIST)
    files = [os.path.relpath(os.path.join(d, f), DIST) for d, _, fs in os.walk(DIST) for f in fs]
    print(f"packaged {len(files)} files into {DIST}")
    return files


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def install(game):
    files = package()
    target = os.path.join(game, "BmGame", "Mods", "BatCoop")
    if not os.path.isdir(os.path.join(game, "Binaries", "Win32", "sdk")):
        sys.exit(f"BmSDK is not installed in {game}")
    os.makedirs(target, exist_ok=True)
    wanted = set(files)
    # Remove our old files that are gone from the package (never the player's ini or logs).
    for d, _, fs in os.walk(target):
        for f in fs:
            rel = os.path.relpath(os.path.join(d, f), target)
            if rel.split(os.sep)[0] in KEEP or rel in wanted:
                continue
            os.remove(os.path.join(target, rel))
            print("removed", rel)
    changed = 0
    for rel in files:
        src, dst = os.path.join(DIST, rel), os.path.join(target, rel)
        if os.path.exists(dst) and sha(src) == sha(dst):
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        try:
            shutil.copy2(src, dst)
        except PermissionError:
            sys.exit(f"{rel} is in use (is BatCoopLink running?); close the game and retry")
        changed += 1
    os.makedirs(os.path.join(ROOT, "install"), exist_ok=True)
    with open(os.path.join(ROOT, "install", "batcoop-installed-files.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join("BmGame/Mods/BatCoop/" + r.replace(os.sep, "/") for r in sorted(files)) + "\n")
    print(f"installed into {target}: {changed} changed, {len(files) - changed} unchanged")


def main():
    args = sys.argv[1:]
    if not args or args[0] not in ("package", "install"):
        sys.exit(__doc__)
    game = DEFAULT_GAME
    if "--game" in args:
        game = args[args.index("--game") + 1]
    if args[0] == "package":
        package()
    else:
        install(game)


if __name__ == "__main__":
    main()
