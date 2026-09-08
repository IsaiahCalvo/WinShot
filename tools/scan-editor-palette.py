"""Scan an editor render PNG and report every chrome colour that is not a theme token.

Usage:  python tools/scan-editor-palette.py <png> [<png> ...]

The rule this enforces is the convergence rule: Survey's mechanics on WinShot's palette.
A pixel in the toolbar chrome that matches none of Theme.xaml's tokens (nor a blend of two
of them, which is what antialiasing and the translucent washes produce) is a styling leak.

Reads the tokens straight out of Theme.xaml so it cannot drift from the palette it checks.
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

# The annotation swatches and the HSV spectrum are user CONTENT, not chrome: a colour
# picker is supposed to show colours the palette does not contain.
CONTENT_OK = True


def theme_colors():
    """Every #AARRGGBB / #RRGGBB literal defined in Theme.xaml, as (r, g, b, a)."""
    text = open(THEME, encoding="utf-8-sig").read()
    out = set()
    for hexv in re.findall(r'#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b', text):
        if len(hexv) == 6:
            a, r, g, b = 255, int(hexv[0:2], 16), int(hexv[2:4], 16), int(hexv[4:6], 16)
        else:
            a, r, g, b = (int(hexv[0:2], 16), int(hexv[2:4], 16),
                          int(hexv[4:6], 16), int(hexv[6:8], 16))
        out.add((r, g, b, a))
    return out


def opaque_forms(tokens):
    """Each token as it can actually land on screen: itself, and composited over each
    other token (which is what an alpha wash or an antialiased edge produces)."""
    solid = {(r, g, b) for (r, g, b, a) in tokens if a == 255}
    forms = set(solid)
    for (r, g, b, a) in tokens:
        if a == 255:
            continue
        f = a / 255.0
        for (br, bg, bb) in solid:
            forms.add((round(r * f + br * (1 - f)),
                       round(g * f + bg * (1 - f)),
                       round(b * f + bb * (1 - f))))
    return forms


def near(px, forms, tol):
    r, g, b = px
    for (tr, tg, tb) in forms:
        if abs(r - tr) <= tol and abs(g - tg) <= tol and abs(b - tb) <= tol:
            return True
    return False


def scan(path, forms, tol=10):
    img = Image.open(path).convert("RGB")
    w, h = img.size
    px = img.load()
    bad = Counter()
    total = 0
    for y in range(h):
        for x in range(w):
            c = px[x, y]
            total += 1
            if not near(c, forms, tol):
                bad[c] += 1
    return w, h, total, bad


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    tokens = theme_colors()
    forms = opaque_forms(tokens)
    print(f"{len(tokens)} tokens in Theme.xaml -> {len(forms)} composited forms\n")
    worst = 0
    for path in sys.argv[1:]:
        w, h, total, bad = scan(path, forms)
        off = sum(bad.values())
        pct = 100.0 * off / total if total else 0
        print(f"{os.path.basename(path)}  {w}x{h}  off-palette {off}/{total} ({pct:.2f}%)")
        for colour, n in bad.most_common(12):
            print("    #%02X%02X%02X  x%d" % (colour[0], colour[1], colour[2], n))
        worst = max(worst, off)
    return 0 if worst == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
