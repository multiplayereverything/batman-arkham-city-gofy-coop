"""Send one command to a game copy's lab bridge and print the JSON answer.

    python -I lab/bridge.py <port> <command...>        e.g. python -I lab/bridge.py 37900 state
    python -I lab/bridge.py <port> --wait <seconds>    wait until the bridge answers
"""
import json
import socket
import sys
import time


def call(port, line, timeout=35):
    with socket.create_connection(("127.0.0.1", port), timeout=5) as s:
        s.settimeout(timeout)
        s.sendall((line + "\n").encode())
        buf = b""
        while b"\n" not in buf:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.split(b"\n", 1)[0].decode())


if __name__ == "__main__":
    port = int(sys.argv[1])
    if sys.argv[2] == "--wait":
        end = time.time() + float(sys.argv[3])
        while time.time() < end:
            try:
                print(json.dumps(call(port, "ping")))
                sys.exit(0)
            except OSError:
                time.sleep(2)
        sys.exit("bridge did not answer")
    print(json.dumps(call(port, " ".join(sys.argv[2:])), indent=1))
