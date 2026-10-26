"""Steam smoke test with one account: BatCoopLink starts Steam as Spacewar (480), creates a tagged friends-only
lobby and leaves it. Steam shows you as "playing Spacewar" for about a second.

    python -I lab/steam_smoke.py
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.path.join(ROOT, "tools", "dotnet", "dotnet.exe")

link = os.path.join(ROOT, "dist", "BatCoop", "link", "BatCoopLink.exe")
if not os.path.exists(link):
    subprocess.run([sys.executable, "-I", os.path.join(ROOT, "lab", "deploy.py"), "package"], check=True)
r = subprocess.run([DOTNET, os.path.join(ROOT, "lab", "Offline", "bin", "Release", "net10.0", "Offline.dll"),
                    "steamsmoke", link, os.path.join(ROOT, "lab", "out", "steam")], timeout=120)
sys.exit(r.returncode)
