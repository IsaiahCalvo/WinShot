#!/usr/bin/env python
"""Rasterise Survey's toolbar glyphs and WinShot's ported XAML side by side and diff them.

Both sides go through ONE rasteriser, so the number it prints is a pure geometry
comparison: same flattening, same anti-aliasing, same grid. What it does NOT measure
is WPF's own rasteriser -- `dotnet build` is what proves the XAML parses; this proves
the numbers in it are Survey's numbers.

    python tools/icon-diff.py              # all icons, writes artifacts/icon-diff/
    python tools/icon-diff.py Highlighter  # just one

ponytail: a ~20px supersampled distance-field rasteriser, not a real SVG engine. It
covers the subset these icons use (path mini-language, rect/circle/line, uniform
stroke per element, round or butt caps, even-odd fill). Reach for cairosvg/resvg if a
glyph ever needs gradients, clips or dashes.
"""
import math
import os
import re
import sys
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SURVEY = r"C:\Users\icalvo\Survey"
XAML = os.path.join(ROOT, "src", "WinShot", "Theme", "SurveyToolIcons.xaml")
OUT = os.path.join(ROOT, "artifacts", "icon-diff")
SIZE, SS = 20, 8  # 20px output, 8x8 samples per pixel

# key -> (survey source, stroke-width fallback). A "file:" source is an .svg under
# Survey's assets; a "jsx:" source is the inline renderer of that name in src/Icons.jsx.
ICONS = {
    "Highlighter": "file:highlighter-tool.svg",
    "Cursor":      "file:selection-cursor-rounded.svg",
    "Pan":         "file:pan-hand-closed.svg",
    "Callout":     "file:callout-arrow-outline.svg",
    "Counter":     "file:counter-outline.svg",
    "DrawGroup":   "jsx:drawGroup",
    "Shapes":      "file:shapes.svg",
    "TextGroup":   "file:case-sensitive.svg",
    "TextBox":     "file:text-box-selection.svg",
    "FitPage":     "jsx:fitPage",
    "FitWidth":    "jsx:fitWidth",
    "FitHeight":   "jsx:fitHeight",
    "Rect":        "jsx:rect",
    "Ellipse":     "jsx:ellipse",
    "Line":        "jsx:line",
    "Arrow":       "jsx:arrow",
    "ChevronDown": "jsx:chevronDown",
    "ChevronUp":   "jsx:chevronUp",
    "ChevronLeft": "jsx:chevronLeft",
    "Text":        "jsx:text",
    "Pen":         "jsx:pen",
    "Eraser":      "jsx:eraser",
    "Undo":        "jsx:undo",
    "Redo":        "jsx:redo",
}

# --------------------------------------------------------------------------- geometry
# A shape is (subpaths, stroke_width, cap, filled) where subpaths is a list of
# (points, closed) and points is a list of (x, y) already in viewBox units.

NUM = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def _bez3(p0, p1, p2, p3, n=24):
    return [(
        (1 - t) ** 3 * p0[0] + 3 * (1 - t) ** 2 * t * p1[0] + 3 * (1 - t) * t * t * p2[0] + t ** 3 * p3[0],
        (1 - t) ** 3 * p0[1] + 3 * (1 - t) ** 2 * t * p1[1] + 3 * (1 - t) * t * t * p2[1] + t ** 3 * p3[1],
    ) for t in (i / n for i in range(1, n + 1))]


def _bez2(p0, p1, p2, n=20):
    return [(
        (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
        (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1],
    ) for t in (i / n for i in range(1, n + 1))]


def _arc(p0, rx, ry, phi, large, sweep, p1, n=36):
    """SVG elliptical arc -> polyline (F.6.5 endpoint-to-centre conversion)."""
    if p0 == p1 or rx == 0 or ry == 0:
        return [p1]
    rx, ry, phi = abs(rx), abs(ry), math.radians(phi)
    cs, sn = math.cos(phi), math.sin(phi)
    dx2, dy2 = (p0[0] - p1[0]) / 2, (p0[1] - p1[1]) / 2
    x1, y1 = cs * dx2 + sn * dy2, -sn * dx2 + cs * dy2
    lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry)
    if lam > 1:
        rx, ry = rx * math.sqrt(lam), ry * math.sqrt(lam)
    num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1
    den = rx * rx * y1 * y1 + ry * ry * x1 * x1
    co = math.sqrt(max(0.0, num / den)) * (1 if large != sweep else -1)
    cx1, cy1 = co * rx * y1 / ry, -co * ry * x1 / rx
    cx = cs * cx1 - sn * cy1 + (p0[0] + p1[0]) / 2
    cy = sn * cx1 + cs * cy1 + (p0[1] + p1[1]) / 2
    th0 = math.atan2((y1 - cy1) / ry, (x1 - cx1) / rx)
    th1 = math.atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx)
    d = th1 - th0
    if not sweep and d > 0:
        d -= 2 * math.pi
    elif sweep and d < 0:
        d += 2 * math.pi
    pts = []
    for i in range(1, n + 1):
        th = th0 + d * i / n
        ex, ey = rx * math.cos(th), ry * math.sin(th)
        pts.append((cs * ex - sn * ey + cx, sn * ex + cs * ey + cy))
    return pts


def parse_path(d):
    """SVG/XAML path mini-language -> [(points, closed)]. Shared by both sides."""
    toks = re.findall(r"[MmLlHhVvCcSsQqTtAaZz]|" + NUM.pattern, d)
    subs, pts, cur, start, i = [], [], (0.0, 0.0), (0.0, 0.0), 0
    cmd, prev_c2, prev_q1 = None, None, None

    def flush(closed=False):
        if len(pts) > 1:
            subs.append((list(pts), closed))

    while i < len(toks):
        t = toks[i]
        if re.match(r"[A-Za-z]", t):
            cmd, i = t, i + 1
        elif cmd in ("M", "m"):
            cmd = "L" if cmd == "M" else "l"  # implicit lineto for extra pairs
        n = lambda k: float(toks[i + k])
        rel = cmd.islower()
        ox, oy = (cur if rel else (0.0, 0.0))
        C = cmd.upper()
        if C == "Z":
            if pts:
                flush(True)
            pts, cur = [], start
            i += 1
            prev_c2 = prev_q1 = None
            continue
        if C == "M":
            flush()
            cur = (n(0) + ox, n(1) + oy)
            start, pts, i = cur, [cur], i + 2
            prev_c2 = prev_q1 = None
            continue
        if C == "L":
            cur = (n(0) + ox, n(1) + oy); i += 2
            prev_c2 = prev_q1 = None
        elif C == "H":
            cur = (n(0) + ox, cur[1]); i += 1
            prev_c2 = prev_q1 = None
        elif C == "V":
            cur = (cur[0], n(0) + oy); i += 1
            prev_c2 = prev_q1 = None
        elif C in ("C", "S"):
            if C == "C":
                c1, c2, e = (n(0) + ox, n(1) + oy), (n(2) + ox, n(3) + oy), (n(4) + ox, n(5) + oy)
                i += 6
            else:
                c1 = (2 * cur[0] - prev_c2[0], 2 * cur[1] - prev_c2[1]) if prev_c2 else cur
                c2, e = (n(0) + ox, n(1) + oy), (n(2) + ox, n(3) + oy)
                i += 4
            pts.extend(_bez3(cur, c1, c2, e))
            cur, prev_c2, prev_q1 = e, c2, None
            continue
        elif C in ("Q", "T"):
            if C == "Q":
                c1, e = (n(0) + ox, n(1) + oy), (n(2) + ox, n(3) + oy)
                i += 4
            else:
                c1 = (2 * cur[0] - prev_q1[0], 2 * cur[1] - prev_q1[1]) if prev_q1 else cur
                e = (n(0) + ox, n(1) + oy)
                i += 2
            pts.extend(_bez2(cur, c1, e))
            cur, prev_q1, prev_c2 = e, c1, None
            continue
        elif C == "A":
            e = (n(5) + ox, n(6) + oy)
            pts.extend(_arc(cur, n(0), n(1), n(2), int(float(toks[i + 3])), int(float(toks[i + 4])), e))
            cur, i = e, i + 7
            prev_c2 = prev_q1 = None
            continue
        else:
            raise ValueError("unhandled command " + str(cmd))
        pts.append(cur)
    flush()
    return subs


def rrect(x, y, w, h, rx, ry=None):
    ry = rx if ry is None else ry
    rx, ry = min(rx, w / 2), min(ry, h / 2)
    if rx <= 0:
        return [([(x, y), (x + w, y), (x + w, y + h), (x, y + h), (x, y)], True)]
    d = (f"M{x + rx} {y}H{x + w - rx}A{rx} {ry} 0 0 1 {x + w} {y + ry}"
         f"V{y + h - ry}A{rx} {ry} 0 0 1 {x + w - rx} {y + h}"
         f"H{x + rx}A{rx} {ry} 0 0 1 {x} {y + h - ry}"
         f"V{y + ry}A{rx} {ry} 0 0 1 {x + rx} {y}Z")
    return parse_path(d)


def ellipse(cx, cy, rx, ry):
    d = (f"M{cx - rx} {cy}A{rx} {ry} 0 0 1 {cx + rx} {cy}"
         f"A{rx} {ry} 0 0 1 {cx - rx} {cy}Z")
    return parse_path(d)


def xform(shapes, m):
    a, b, c, d, e, f = m
    return [([([(a * x + c * y + e, b * x + d * y + f) for x, y in p], cl) for p, cl in sp], w, cap, fl)
            for sp, w, cap, fl in shapes]


def mul(m1, m2):
    a1, b1, c1, d1, e1, f1 = m1
    a2, b2, c2, d2, e2, f2 = m2
    return (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2, a1 * c2 + c1 * d2,
            b1 * c2 + d1 * d2, a1 * e2 + c1 * f2 + e1, b1 * e2 + d1 * f2 + f1)


def rot(deg, cx=0.0, cy=0.0):
    r = math.radians(deg)
    cs, sn = math.cos(r), math.sin(r)
    return mul((1, 0, 0, 1, cx, cy), mul((cs, sn, -sn, cs, 0, 0), (1, 0, 0, 1, -cx, -cy)))


def scale(sx, sy, cx=0.0, cy=0.0):
    return mul((1, 0, 0, 1, cx, cy), mul((sx, 0, 0, sy, 0, 0), (1, 0, 0, 1, -cx, -cy)))


# ------------------------------------------------------------------------- rasteriser

def raster(shapes, box, size=SIZE, ss=SS):
    """shapes in `box`-unit space -> float alpha array, size x size, 0..1."""
    n = size * ss
    step = box / n
    gx = (np.arange(n) + 0.5) * step
    X, Y = np.meshgrid(gx, gx)
    hit = np.zeros((n, n), dtype=bool)
    for subs, w, cap, filled in shapes:
        if filled:
            # even-odd crossing count over all subpaths
            cross = np.zeros((n, n), dtype=np.int32)
            for pts, _ in subs:
                p = np.asarray(pts + [pts[0]], dtype=np.float64)
                for (x0, y0), (x1, y1) in zip(p[:-1], p[1:]):
                    if y0 == y1:
                        continue
                    t = (Y - y0) / (y1 - y0)
                    cross += (((Y >= min(y0, y1)) & (Y < max(y0, y1))) & (X < x0 + t * (x1 - x0)))
            hit |= (cross % 2) == 1
        if w <= 0:
            continue
        r = w / 2.0
        for pts, closed in subs:
            p = np.asarray(pts + ([pts[0]] if closed else []), dtype=np.float64)
            for (x0, y0), (x1, y1) in zip(p[:-1], p[1:]):
                dx, dy = x1 - x0, y1 - y0
                L2 = dx * dx + dy * dy
                if L2 == 0:
                    if cap == "round":
                        hit |= ((X - x0) ** 2 + (Y - y0) ** 2) <= r * r
                    continue
                t = ((X - x0) * dx + (Y - y0) * dy) / L2
                if cap == "butt" and not closed and len(p) == 2:
                    inside = (t >= 0) & (t <= 1)
                else:
                    t = np.clip(t, 0.0, 1.0)
                    inside = True
                px, py = x0 + t * dx, y0 + t * dy
                hit |= inside & (((X - px) ** 2 + (Y - py) ** 2) <= r * r)
    return hit.reshape(size, ss, size, ss).mean(axis=(1, 3))


# ------------------------------------------------------------------------ survey side

CAMEL = re.compile(r"([a-z])([A-Z])")


def _attrs(el, inherited):
    a = dict(inherited)
    for k, v in el.attrib.items():
        a[CAMEL.sub(r"\1-\2", k).lower()] = v
    return a


def _num(a, k, dflt=0.0):
    v = a.get(k)
    return float(v) if v not in (None, "") else dflt


def _stroke(a):
    w = a.get("stroke-width")
    w = float(w) if w else 1.0
    if a.get("stroke") in (None, "none"):
        w = 0.0
    cap = "butt" if a.get("stroke-linecap", "butt") == "butt" else "round"
    return w, cap


def _svg_tree(el, inherited, mat, out):
    tag = el.tag.split("}")[-1]
    a = _attrs(el, inherited)
    m = mat
    if "transform" in el.attrib:
        m = mul(m, _css_transform(el.attrib["transform"]))
    if tag in ("svg", "g", "defs", "mask"):
        for ch in el:
            _svg_tree(ch, a, m, out)
        return
    w, cap = _stroke(a)
    filled = a.get("fill") not in (None, "none")
    subs = None
    if tag == "path":
        subs = parse_path(el.attrib["d"])
    elif tag == "rect":
        subs = rrect(_num(a, "x"), _num(a, "y"), _num(a, "width"), _num(a, "height"),
                     _num(a, "rx"), _num(a, "ry") or _num(a, "rx"))
    elif tag == "circle":
        subs = ellipse(_num(a, "cx"), _num(a, "cy"), _num(a, "r"), _num(a, "r"))
    elif tag == "ellipse":
        subs = ellipse(_num(a, "cx"), _num(a, "cy"), _num(a, "rx"), _num(a, "ry"))
    elif tag == "line":
        subs = [([(_num(a, "x1"), _num(a, "y1")), (_num(a, "x2"), _num(a, "y2"))], False)]
    elif tag == "polyline":
        v = [float(x) for x in NUM.findall(a.get("points", ""))]
        subs = [(list(zip(v[::2], v[1::2])), False)]
    if subs:
        out.extend(xform([(subs, w, cap, filled)], m))


def _css_transform(s):
    m = (1, 0, 0, 1, 0, 0)
    for name, args in re.findall(r"(\w+)\(([^)]*)\)", s):
        v = [float(x) for x in NUM.findall(args)]
        if name == "translate":
            m = mul(m, (1, 0, 0, 1, v[0], v[1] if len(v) > 1 else 0))
        elif name == "scale":
            m = mul(m, (v[0], 0, 0, v[1] if len(v) > 1 else v[0], 0, 0))
        elif name in ("rotate", "rotateZ"):
            m = mul(m, rot(v[0], v[1], v[2]) if len(v) > 2 else rot(v[0]))
        elif name == "scaleY":
            m = mul(m, (1, 0, 0, v[0], 0, 0))
        elif name == "scaleX":
            m = mul(m, (v[0], 0, 0, 1, 0, 0))
        elif name == "matrix":
            m = mul(m, tuple(v))
    return m


def survey_shapes(src):
    kind, name = src.split(":", 1)
    if kind == "file":
        xml = open(os.path.join(SURVEY, "src", "assets", "icons", name), encoding="utf-8").read()
    else:
        jsx = open(os.path.join(SURVEY, "src", "Icons.jsx"), encoding="utf-8").read()
        i = jsx.index("\n    %s: (size," % name)
        body = jsx[jsx.index("<svg", i):]
        body = body[:body.index("</svg>") + 6]
        # JSX -> XML: drop {expr} attribute values and self-closing oddities
        xml = re.sub(r"=\{[^{}]*\}", '="X"', body)
        xml = re.sub(r"style=\"X\"", "", xml)
        xml = re.sub(r"\{/\*.*?\*/\}", "", xml, flags=re.S)
        # the style prop carries the CSS transform; pull it out by hand
        st = re.search(r"style=\{\{[^}]*transform:\s*'([^']+)'", body)
        if st:
            xml = xml.replace("<svg", '<svg data-css-transform="%s"' % st.group(1), 1)
        xml = re.sub(r"style=\{\{.*?\}\}", "", xml, flags=re.S)
    xml = re.sub(r"<!--.*?-->", "", xml, flags=re.S)
    xml = re.sub(r"<\?xml.*?\?>", "", xml, flags=re.S)
    root = ET.fromstring(xml)
    vb = [float(x) for x in NUM.findall(root.attrib.get("viewBox", "0 0 24 24"))]
    box = max(vb[2], vb[3])
    base = (1, 0, 0, 1, -vb[0], -vb[1])
    css = root.attrib.get("data-css-transform")
    if css:  # CSS transforms are about the element centre, not the origin
        c = box / 2.0
        base = mul(mul((1, 0, 0, 1, c, c), mul(_css_transform(css), (1, 0, 0, 1, -c, -c))), base)
    out = []
    _svg_tree(root, {}, base, out)
    return [s for s in out if s[1] > 0 or s[3]], box


# ----------------------------------------------------------------------- winshot side

NS = {"x": "http://schemas.microsoft.com/winfx/2006/xaml"}
DEF_W, DEF_CAP = 1.5, "round"


def _wpf_transform(el):
    # WPF composes a TransformGroup row-vector style: Children[0] is applied FIRST,
    # which is the reverse of a CSS transform list. Hence mul(child, m), not mul(m, child).
    m = (1, 0, 0, 1, 0, 0)
    for t in el:
        tag = t.tag.split("}")[-1]
        g = lambda k, d=0.0: float(t.attrib.get(k, d))
        if tag == "TransformGroup":
            m = mul(_wpf_transform(t), m)
        elif tag == "TranslateTransform":
            m = mul((1, 0, 0, 1, g("X"), g("Y")), m)
        elif tag == "ScaleTransform":
            m = mul(scale(g("ScaleX", 1), g("ScaleY", 1), g("CenterX"), g("CenterY")), m)
        elif tag == "RotateTransform":
            m = mul(rot(g("Angle"), g("CenterX"), g("CenterY")), m)
    return m


def _wpf_walk(el, mat, out):
    tag = el.tag.split("}")[-1]
    if tag.endswith(".RenderTransform"):
        return
    m = mat
    rt = el.find("{*}%s.RenderTransform" % tag)
    if rt is not None:
        m = mul(m, _wpf_transform(rt))
    if tag == "Canvas":
        for ch in el:
            _wpf_walk(ch, m, out)
        return
    a = el.attrib
    style = a.get("Style", "")
    fill_style = "SurveyIconFill" in style  # that style nulls Stroke
    # an explicit Fill= on a stroked path (the Undo/Redo arrowheads) fills AND strokes
    filled = fill_style or a.get("Fill", "Transparent") != "Transparent"
    w = 0.0 if fill_style else float(a.get("StrokeThickness", DEF_W))
    cap = "butt" if a.get("StrokeStartLineCap") == "Flat" else DEF_CAP
    left, top = float(a.get("{*}Left", a.get("Canvas.Left", 0)) or 0), float(a.get("Canvas.Top", 0) or 0)
    subs = None
    if tag == "Path":
        subs = parse_path(a["Data"])
    elif tag == "Rectangle":
        subs = rrect(left, top, float(a["Width"]), float(a["Height"]),
                     float(a.get("RadiusX", 0)), float(a.get("RadiusY", 0)))
    elif tag == "Ellipse":
        ww, hh = float(a["Width"]), float(a["Height"])
        subs = ellipse(left + ww / 2, top + hh / 2, ww / 2, hh / 2)
    if subs:
        out.extend(xform([(subs, w, cap, filled)], m))


def winshot_shapes(key):
    src = open(XAML, encoding="utf-8-sig").read()
    # Canvas.Left/Canvas.Top are attached properties; ET keeps them verbatim
    root = ET.fromstring(src)
    for vb in root.iter():
        if vb.tag.split("}")[-1] == "Viewbox" and vb.attrib.get("{%s}Key" % NS["x"]) == "SurveyIcon." + key:
            canvas = list(vb)[0]
            box = max(float(canvas.attrib["Width"]), float(canvas.attrib["Height"]))
            out = []
            _wpf_walk(canvas, (1, 0, 0, 1, 0, 0), out)
            return out, box
    raise KeyError("SurveyIcon." + key)


# ------------------------------------------------------------------------------- main

def png(a, path):
    Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8), "L").resize(
        (SIZE * 8, SIZE * 8), Image.NEAREST).save(path)


def main(argv):
    os.makedirs(OUT, exist_ok=True)
    keys = argv or list(ICONS)
    print(f"{'icon':<13} {'match%':>7} {'mean|d|':>8} {'max|d|':>7}  survey source")
    print("-" * 78)
    worst = []
    for k in keys:
        src = ICONS[k]
        s_shapes, s_box = survey_shapes(src)
        w_shapes, w_box = winshot_shapes(k)
        a = raster(s_shapes, s_box)
        b = raster(w_shapes, w_box)
        d = np.abs(a - b)
        match = float(((a >= 0.5) == (b >= 0.5)).mean() * 100)
        png(a, os.path.join(OUT, f"{k}.survey.png"))
        png(b, os.path.join(OUT, f"{k}.winshot.png"))
        png(1 - d, os.path.join(OUT, f"{k}.diff.png"))
        print(f"{k:<13} {match:7.2f} {d.mean():8.4f} {d.max():7.3f}  {src}")
        worst.append((match, k))
    worst.sort()
    if len(worst) > 1:
        print(f"\nworst: {worst[0][1]} at {worst[0][0]:.2f}%   PNGs in {OUT}")
    # self-check: an icon must at minimum match itself
    a = raster(*survey_shapes("file:shapes.svg"))
    assert np.abs(a - raster(*survey_shapes("file:shapes.svg"))).max() == 0, "rasteriser not deterministic"
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
