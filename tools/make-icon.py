"""Draw Fetchr's app icon (arrow into a tray on a rounded accent square) and write a multi-size .ico.

    python tools/make-icon.py installer/fetchr.ico

Standard library only: shapes are rasterised with 4x4 supersampling and stored as PNG frames inside
the ICO, which Windows Vista+ reads. The accent is the design token's placeholder blue until rebranding.
"""
import math
import struct
import sys
import zlib

ACCENT = (0x2A, 0x5B, 0xD7)
SIZES = [16, 24, 32, 48, 64, 128, 256]


def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def inside_rounded(x, y, r):
    # unit square 0..1 with corner radius r
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return math.hypot(x - cx, y - cy) <= r


# White strokes in unit coordinates: shaft, arrow head, tray line.
STROKES = [((0.5, 0.20), (0.5, 0.60)), ((0.31, 0.43), (0.5, 0.62)), ((0.69, 0.43), (0.5, 0.62)), ((0.27, 0.78), (0.73, 0.78))]
WIDTH = 0.095


def pixel(size, x, y):
    ss = 4
    bg = fg = 0
    for i in range(ss):
        for j in range(ss):
            u = (x + (i + 0.5) / ss) / size
            v = (y + (j + 0.5) / ss) / size
            if inside_rounded(u, v, 0.22):
                bg += 1
                if any(seg_dist(u, v, *a, *b) <= WIDTH / 2 for a, b in STROKES):
                    fg += 1
    n = ss * ss
    alpha = bg / n
    if alpha == 0:
        return (0, 0, 0, 0)
    w = fg / bg
    rgb = tuple(round(c * (1 - w) + 255 * w) for c in ACCENT)
    return (*rgb, round(alpha * 255))


def png(size):
    rows = b"".join(b"\x00" + b"".join(bytes(pixel(size, x, y)) for x in range(size)) for y in range(size))
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def main():
    frames = [png(s) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    for s, data in zip(SIZES, frames):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(sys.argv[1], "wb") as f:
        f.write(header + entries + b"".join(frames))
    print("wrote", sys.argv[1], [len(f) for f in frames])


if __name__ == "__main__":
    main()
