"""Take an in-game screenshot through the lab bridge and save it as PNG.

    python -I lab/shot.py <bridge port> <out.png>
"""
import glob
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(os.path.expanduser("~"), "Documents", "WB Games", "Batman Arkham City GOTY", "BmGame", "ScreenShots")

if __name__ == "__main__":
    port, out = sys.argv[1], sys.argv[2]
    before = set(glob.glob(os.path.join(SHOTS, "*.bmp")))
    subprocess.run([sys.executable, "-I", os.path.join(HERE, "bridge.py"), port, "exec", "shot"], capture_output=True)
    end = time.time() + 10
    new = []
    while time.time() < end and not new:
        time.sleep(0.5)
        new = sorted(set(glob.glob(os.path.join(SHOTS, "*.bmp"))) - before, key=os.path.getmtime)
    if not new:
        sys.exit("no screenshot appeared")
    time.sleep(0.5)
    subprocess.run([sys.executable, "-I", os.path.join(HERE, "bmp2png.py"), new[-1], out], check=True)
    os.remove(new[-1])
    print(out)
