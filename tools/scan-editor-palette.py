"""Scan an editor render and report every CHROME pixel that is not a theme colour.

Usage:
  python tools/scan-editor-palette.py <png> [<png> ...]          whole image
  python tools/scan-editor-palette.py --band T:B <png> [...]     rows T..B only

The rule this enforces is the convergence rule: Survey's mechanics on WinShot's palette. A
pixel in the toolbar chrome that is neither a theme token nor a blend of two of them is a
styling leak. Blends have to be allowed: antialiasing, the translucent hover/press washes
and the popover shadows all produce colours strictly between two token colours, and every
one of those is the palette doing its job.

So the test is: is this pixel within `tol` of the line segment between SOME pair of theme
colours? That is exact for a two-colour edge and generous enough for a wash over one
ground, and it still rejects a colour that is not on any such segment — which is what a
foreign hue like Survey's #d8a84e gold or #e8e2d4 paper cream is.

Reads the tokens straight out of Theme.xaml so it cannot drift from the palette it checks.
Point it at the CHROME: a whole editor render includes the user's screenshot, and a
screenshot is content, not palette.
"""
import re
import sys
import os
from collections import Counter

try:
    from PIL import Image
except ImportError:
    sys.exit("needs Pillow: python -m pip install pillow")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
THEME = os.path.join(ROOT, "src", "WinShot", "Theme", "Theme.xaml")


def theme_colors():
    """Every #RRGGBB / #AARRGGBB literal in Theme.xaml, flattened to opaque RGB.

    A translucent token is included both as its own RGB and composited over each opaque
    one, because both forms really appear on screen.
    """
    text = open(THEME, encoding="utf-8-sig").read()
    solid, washes = set(), []
    for hexv in re.findall(r"#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b", text):
        if len(hexv) == 6:
            solid.add((int(hexv[0:2], 16), int(hexv[2:4], 16), int(hexv[4:6], 16)))
        else:
            a = int(hexv[0:2], 16)
            rgb = (int(hexv[2:4], 16), int(hexv[4:6], 16), int(hexv[6:8], 16))
            (solid if a == 255 else washes).add(rgb) if a == 255 else washes.append((a, rgb))
    out = set(solid)
    for a, (r, g, b) in washes:
        f = a / 255.0
        out.add((r, g, b))
        for (br, bg, bb) in solid:
            out.add((round(r * f + br * (1 - f)),
                     round(g * f + bg * (1 - f)),
                     round(b * f + bb * (1 - f))))
    # Pure black and white are the scrims every dark UI uses; they are in the file already,
    # but be explicit so a render's shadow edge is never counted as foreign.
    out.add((0, 0, 0))
    out.add((255, 255, 255))
    return sorted(out)


def seg_dist2(p, a, b):
    """Squared distance from point p to the segment ab, in RGB."""
    ax, ay, az = a
    bx, by, bz = b
    px, py, pz = p
    dx, dy, dz = bx - ax, by - ay, bz - az
    den = dx * dx + dy * dy + dz * dz
    if den == 0:
        return (px - ax) ** 2 + (py - ay) ** 2 + (pz - az) ** 2
    t = ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / den
    t = 0.0 if t < 0 else 1.0 if t > 1 else t
    qx, qy, qz = ax + dx * t, ay + dy * t, az + dz * t
    return (px - qx) ** 2 + (py - qy) ** 2 + (pz - qz) ** 2


def on_palette(px, colors, tol2, cache):
    hit = cache.get(px)
    if hit is not None:
        return hit
    ok = False
    for i, a in enumerate(colors):
        if (px[0] - a[0]) ** 2 + (px[1] - a[1]) ** 2 + (px[2] - a[2]) ** 2 <= tol2:
            ok = True
            break
        for b in colors[i + 1:]:
            if seg_dist2(px, a, b) <= tol2:
                ok = True
                break
        if ok:
            break
    cache[px] = ok
    return ok


def main():
    args = sys.argv[1:]
    band = None
    if args and args[0] == "--band":
        top, bottom = args[1].split(":")
        band = (int(top), int(bottom))
        args = args[2:]
    if not args:
        sys.exit(__doc__)

    colors = theme_colors()
    tol2 = 8 * 8
    print(f"{len(colors)} theme colours (tokens + their washes over each ground)\n")

    worst = 0
    for path in args:
        img = Image.open(path).convert("RGB")
        w, h = img.size
        y0, y1 = band if band else (0, h)
        y1 = min(y1, h)
        px = img.load()
        bad = Counter()
        total = 0
        cache = {}
        for y in range(y0, y1):
            for x in range(w):
                c = px[x, y]
                total += 1
                if not on_palette(c, colors, tol2, cache):
                    bad[c] += 1
        off = sum(bad.values())
        pct = 100.0 * off / total if total else 0
        rows = f"rows {y0}-{y1}" if band else "whole image"
        print(f"{os.path.basename(path)}  {w}x{h}  {rows}  off-palette {off}/{total} ({pct:.3f}%)")
        for colour, n in bad.most_common(10):
            print("    #%02X%02X%02X  x%d" % (colour[0], colour[1], colour[2], n))
        worst = max(worst, off)
    return 0 if worst == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
