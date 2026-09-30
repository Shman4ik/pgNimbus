#!/usr/bin/env python3
"""Writes src/PgNimbus.App/Styles/LogoMark.axaml from design/logo.svg.

The app draws the mark as vector geometry (a DrawingImage keyed LogoMarkImage),
so it is sharp at any size and on any DPI, instead of shipping another raster
that would have to be kept in step with the master. Like everything downstream
of design/logo.svg this is generated: edit the .af, regenerate the SVG with
af-to-svg.py, then run this. Never hand-edit the output.

It is a transcription, not an interpretation, and that only works because
logo.svg is flat by contract (see its header): plain <circle> and <path>
elements in the root coordinate system, colour given as a plain fill/stroke
attribute, no transforms, masks or <use>. A clearance group's stroke settings
sit on the <g>, and apply to every path in it.

    python scripts/design/svg-to-axaml.py
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "design" / "logo.svg"
TARGET = ROOT / "src" / "PgNimbus.App" / "Styles" / "LogoMark.axaml"
NS = {"svg": "http://www.w3.org/2000/svg"}

CAPS = {"round": "Round", "square": "Square", "butt": "Flat"}
JOINS = {"round": "Round", "bevel": "Bevel", "miter": "Miter"}


def drawing(geometry: str, attrs: dict[str, str]) -> str:
    fill = attrs.get("fill")
    stroke = attrs.get("stroke")
    brush = f' Brush="{fill}"' if fill and fill != "none" else ""
    pen = ""
    if stroke and stroke != "none":
        pen = (
            "\n                    <GeometryDrawing.Pen>"
            f'\n                        <Pen Brush="{stroke}" Thickness="{attrs.get("stroke-width", "1")}"'
            f' LineJoin="{JOINS[attrs.get("stroke-linejoin", "miter")]}"'
            f' LineCap="{CAPS[attrs.get("stroke-linecap", "butt")]}" />'
            "\n                    </GeometryDrawing.Pen>"
        )
    if pen:
        return (
            f'                <GeometryDrawing{brush} Geometry="{geometry}">{pen}'
            "\n                </GeometryDrawing>"
        )
    return f'                <GeometryDrawing{brush} Geometry="{geometry}" />'


def circle_path(cx: float, cy: float, r: float) -> str:
    # Two arcs: the path mini-language has no circle primitive.
    return f"M{cx - r},{cy} A{r},{r} 0 1 0 {cx + r},{cy} A{r},{r} 0 1 0 {cx - r},{cy} Z"


def walk(node: ET.Element, inherited: dict[str, str], out: list[str]) -> None:
    attrs = dict(inherited)
    for key in ("fill", "stroke", "stroke-width", "stroke-linejoin", "stroke-linecap"):
        if key in node.attrib:
            attrs[key] = node.attrib[key]

    tag = node.tag.split("}")[-1]
    if tag == "path":
        out.append(drawing(re.sub(r"\s+", " ", node.attrib["d"]).strip(), attrs))
    elif tag == "circle":
        cx, cy, r = (float(node.attrib[k]) for k in ("cx", "cy", "r"))
        out.append(drawing(circle_path(cx, cy, r), attrs))
    elif tag in ("svg", "g"):
        for child in node:
            walk(child, attrs, out)
    elif tag not in ("title", "style", "desc"):
        sys.exit(f"logo.svg has a <{tag}>, which this transcription does not handle")


def main() -> None:
    svg = ET.parse(SOURCE).getroot()
    if svg.attrib.get("viewBox") != "0 0 1024 1024":
        sys.exit("expected viewBox 0 0 1024 1024")

    drawings: list[str] = []
    walk(svg, {}, drawings)

    TARGET.write_text(
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"\n'
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        "\n"
        "    <!-- GENERATED from design/logo.svg by scripts/design/svg-to-axaml.py.\n"
        "         Do not edit: regenerate. The mark is plated (a dark disc holding a\n"
        "         light field), so it reads on either theme with fixed colours. -->\n"
        '    <DrawingImage x:Key="LogoMarkImage">\n'
        "        <DrawingImage.Drawing>\n"
        "            <DrawingGroup>\n"
        + "\n".join(drawings)
        + "\n            </DrawingGroup>\n"
        "        </DrawingImage.Drawing>\n"
        "    </DrawingImage>\n"
        "\n"
        "</ResourceDictionary>\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"Wrote {TARGET.relative_to(ROOT)} ({len(drawings)} drawings)")


if __name__ == "__main__":
    main()
