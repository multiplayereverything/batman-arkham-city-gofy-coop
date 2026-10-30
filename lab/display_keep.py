"""Keep the player's resolution through lab runs.

Lab copies run in a window (-windowed -ResX -ResY), and the game saves its window's size as the resolution
when it quits (BmEngine.ini [SystemSettings] Fullscreen/ResX/ResY). save() remembers the player's values
before a lab run, restore() writes them back after it (only while no game copy runs).

    python -I lab/display_keep.py show|save|restore
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INI = os.path.join(os.path.expanduser("~"), "Documents", "WB Games", "Batman Arkham City GOTY", "BmGame", "Config", "BmEngine.ini")
STATE = os.path.join(ROOT, "local", "display.json")
KEYS = ("Fullscreen", "ResX", "ResY")


def _section(text):
    m = re.search(r"^\[SystemSettings\]\r?\n", text, re.M)
    if not m:
        return None
    end = re.search(r"^\[", text[m.end():], re.M)
    return m.end(), m.end() + (end.start() if end else len(text) - m.end())


def read():
    with open(INI, newline="") as f:
        text = f.read()
    s = _section(text)
    if not s:
        return {}
    return {k: v for k, v in re.findall(r"^(Fullscreen|ResX|ResY)=(.*?)\r?$", text[s[0]:s[1]], re.M)}


def write(values):
    with open(INI, newline="") as f:
        text = f.read()
    s = _section(text)
    if not s:
        return False
    body = text[s[0]:s[1]]
    for k, v in values.items():
        body = re.sub(rf"^{k}=.*?(\r?)$", lambda m: f"{k}={v}{m.group(1)}", body, flags=re.M)
    with open(INI, "w", newline="") as f:
        f.write(text[:s[0]] + body + text[s[1]:])
    return True


def save():
    """Remember the player's values, unless a lab run already did (its values are the player's)."""
    if os.path.exists(STATE) or not os.path.exists(INI):
        return
    values = read()
    if int(values.get("ResX", "0")) < 640 or int(values.get("ResY", "0")) < 360:
        return  # already broken: nothing worth keeping (the mod's DisplayGuard repairs it)
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    json.dump(values, open(STATE, "w"))


def restore():
    if not os.path.exists(STATE):
        return None
    values = json.load(open(STATE))
    if values and os.path.exists(INI):
        write(values)
    os.remove(STATE)
    return values


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "show"
    if cmd == "save":
        save()
    elif cmd == "restore":
        print("restored", restore())
    else:
        print(read(), "saved:", json.load(open(STATE)) if os.path.exists(STATE) else None)
