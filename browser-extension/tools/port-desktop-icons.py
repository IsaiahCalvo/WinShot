"""Port WinShot desktop's Survey tool glyphs (src/WinShot/Theme/SurveyToolIcons.xaml)
to inline SVG strings for the browser extension, so both editors share one icon set.

Run from the repo root:  python3 browser-extension/tools/port-desktop-icons.py
Writes browser-extension/src/icons.js. Re-run whenever the desktop glyphs change.
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/WinShot/Theme/SurveyToolIcons.xaml"
TARGET = ROOT / "browser-extension/src/icons.js"
WPF = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"

STROKE = {"stroke": "currentColor", "stroke-width": "1.667", "stroke-linecap": "round",
          "stroke-linejoin": "round", "fill": "none"}
FILL = {"fill": "currentColor", "stroke": "none"}


def num(value):
    return f"{float(value):g}"


def colour(value):
    if value is None:
        return None
    if value.startswith("{"):
        return "currentColor" if "Foreground" in value else "none"
    if value in ("Transparent", "{x:Null}"):
        return "none"
    return value


def transform_of(element):
    node = element.find(f"{WPF}{element.tag[len(WPF):]}.RenderTransform")
    if node is None:
        return ""
    parts = []
    def one(t):
        name = t.tag[len(WPF):]
        if name == "TransformGroup":
            for child in t:
                one(child)
        elif name == "TranslateTransform":
            parts.append(f"translate({num(t.get('X', 0))} {num(t.get('Y', 0))})")
        elif name == "ScaleTransform":
            cx, cy = float(t.get("CenterX", 0)), float(t.get("CenterY", 0))
            sx, sy = float(t.get("ScaleX", 1)), float(t.get("ScaleY", 1))
            parts.append(f"translate({cx:g} {cy:g}) scale({sx:g} {sy:g}) translate({-cx:g} {-cy:g})")
        elif name == "RotateTransform":
            parts.append(f"rotate({num(t.get('Angle', 0))} {num(t.get('CenterX', 0))} {num(t.get('CenterY', 0))})")
    for child in node:
        one(child)
    # WPF applies a TransformGroup's children first-to-last; SVG composes right-to-left.
    return " ".join(reversed(parts))


def paint(element):
    style = element.get("Style", "")
    attrs = dict(FILL if "Fill" in style else STROKE)
    if element.get("StrokeThickness"):
        attrs["stroke-width"] = num(element.get("StrokeThickness"))
    if element.get("Fill") is not None:
        attrs["fill"] = colour(element.get("Fill"))
    if element.get("Stroke") is not None:
        attrs["stroke"] = colour(element.get("Stroke"))
    if element.get("Opacity"):
        attrs["opacity"] = num(element.get("Opacity"))
    if element.get("FillRule") == "EvenOdd":
        attrs["fill-rule"] = "evenodd"
    for cap in ("StrokeStartLineCap", "StrokeEndLineCap"):
        if element.get(cap):
            attrs["stroke-linecap"] = element.get(cap).lower()
    transform = transform_of(element)
    left, top = element.get("Canvas.Left"), element.get("Canvas.Top")
    if left or top:
        transform = f"translate({num(left or 0)} {num(top or 0)}) {transform}".strip()
    if transform:
        attrs["transform"] = transform
    return attrs


def attr_text(attrs):
    return " ".join(f'{k}="{v}"' for k, v in attrs.items() if v is not None)


def path_data(text):
    text = re.sub(r"^\s*F[01]\s*", "", text.strip())
    return text


def geometry(node):
    name = node.tag[len(WPF):]
    if name == "EllipseGeometry":
        cx, cy = (float(v) for v in node.get("Center", "0,0").split(","))
        return "ellipse", {"cx": f"{cx:g}", "cy": f"{cy:g}", "rx": num(node.get("RadiusX", 0)), "ry": num(node.get("RadiusY", 0))}
    if name == "RectangleGeometry":
        x, y, w, h = (float(v) for v in node.get("Rect").split(","))
        attrs = {"x": f"{x:g}", "y": f"{y:g}", "width": f"{w:g}", "height": f"{h:g}"}
        if node.get("RadiusX"):
            attrs["rx"] = num(node.get("RadiusX"))
            attrs["ry"] = num(node.get("RadiusY", node.get("RadiusX")))
        return "rect", attrs
    if name == "PathGeometry":
        attrs = {"d": path_data(node.get("Figures", ""))}
        if node.get("FillRule") == "EvenOdd":
            attrs["fill-rule"] = "evenodd"
        return "path", attrs
    raise ValueError(f"unsupported geometry {name}")


def geometry_transform(node):
    name = node.tag[len(WPF):]
    holder = node.find(f"{WPF}{name}.Transform")
    if holder is None:
        return ""
    fake = ET.Element(f"{WPF}Path")
    rt = ET.SubElement(fake, f"{WPF}Path.RenderTransform")
    for child in holder:
        rt.append(child)
    return transform_of(fake)


def geometry_group(group, attrs, key):
    """An even-odd GeometryGroup is a solid figure with the later figures cut out of it.
    SVG cannot even-odd across elements, so the cut-outs become a mask."""
    children = list(group)
    tag, geo = geometry(children[0])
    mask_id = f"ws-{key.lower()}-cut"
    cuts = ""
    for child in children[1:]:
        ctag, cgeo = geometry(child)
        t = geometry_transform(child)
        cuts += f'<{ctag} {attr_text(cgeo)} fill="black"' + (f' transform="{t}"' if t else "") + "/>"
    mask = f'<mask id="{mask_id}" maskUnits="userSpaceOnUse"><rect x="-10000" y="-10000" width="20000" height="20000" fill="white"/>{cuts}</mask>'
    return f'<defs>{mask}</defs><{tag} {attr_text(geo)} {attr_text(attrs)} mask="url(#{mask_id})"/>'


def shape(element, key=""):
    name = element.tag[len(WPF):]
    attrs = paint(element)
    if name == "Path":
        if element.get("Data"):
            return f'<path d="{path_data(element.get("Data"))}" {attr_text(attrs)}/>'
        data = element.find(f"{WPF}Path.Data")
        first = list(data)[0]
        if first.tag == f"{WPF}GeometryGroup":
            return geometry_group(first, attrs, key)
        tag, geo = geometry(first)
        return f"<{tag} {attr_text(geo)} {attr_text(attrs)}/>"
    if name in ("Ellipse", "Rectangle"):
        w, h = float(element.get("Width", 0)), float(element.get("Height", 0))
        if name == "Ellipse":
            return f'<ellipse cx="{w/2:g}" cy="{h/2:g}" rx="{w/2:g}" ry="{h/2:g}" {attr_text(attrs)}/>'
        extra = f' rx="{num(element.get("RadiusX"))}"' if element.get("RadiusX") else ""
        return f'<rect width="{w:g}" height="{h:g}"{extra} {attr_text(attrs)}/>'
    if name == "Canvas":
        inner = "".join(shape(child) for child in element if not child.tag.endswith("RenderTransform"))
        transform = paint(element).get("transform")
        return f'<g transform="{transform}">{inner}</g>' if transform else f"<g>{inner}</g>"
    if "." in name:
        return ""
    raise ValueError(f"unsupported element {name}")


def main():
    tree = ET.parse(SOURCE)
    icons = {}
    for box in tree.getroot().iter(f"{WPF}Viewbox"):
        key = box.get(f"{X}Key", "")
        if not key.startswith("SurveyIcon."):
            continue
        canvas = box.find(f"{WPF}Canvas")
        if canvas is None:
            continue
        w, h = num(canvas.get("Width", 24)), num(canvas.get("Height", 24))
        try:
            body = "".join(shape(child, key[len("SurveyIcon."):]) for child in canvas)
        except ValueError as error:
            print(f"skip {key}: {error}", file=sys.stderr)
            continue
        icons[key[len("SurveyIcon."):]] = f'<svg viewBox="0 0 {w} {h}" aria-hidden="true">{body}</svg>'
    banner = ("// Generated by tools/port-desktop-icons.py from WinShot desktop's\n"
              "// src/WinShot/Theme/SurveyToolIcons.xaml. Do not edit by hand.\n")
    TARGET.write_text(banner + "export const ICONS = Object.freeze(" + json.dumps(icons, indent=2) + ");\n", encoding="utf-8")
    print(f"wrote {len(icons)} icons to {TARGET.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
