"""Convert the game's 24-bit BMP screenshots to PNG (standard library only): bmp2png.py in.bmp out.png"""
import struct
import sys
import zlib


def convert(src, dst):
    d = open(src, "rb").read()
    off = struct.unpack_from("<I", d, 10)[0]
    w, h = struct.unpack_from("<ii", d, 18)
    bpp = struct.unpack_from("<H", d, 28)[0]
    step = bpp // 8
    stride = ((w * step) + 3) & ~3
    rows = []
    for y in range(abs(h)):
        sy = abs(h) - 1 - y if h > 0 else y
        row = d[off + sy * stride: off + sy * stride + w * step]
        px = bytearray(w * 3)
        px[0::3] = row[2::step]
        px[1::3] = row[1::step]
        px[2::3] = row[0::step]
        rows.append(b"\x00" + bytes(px))

    def chunk(t, b):
        return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xFFFFFFFF)

    with open(dst, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, abs(h), 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(b"".join(rows), 6)) + chunk(b"IEND", b""))


if __name__ == "__main__":
    convert(sys.argv[1], sys.argv[2])
