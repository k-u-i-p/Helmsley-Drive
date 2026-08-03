#!/usr/bin/env python3
"""Build the app icon from the portal's own H mark, into both targets' asset catalogs.

Run after the mark changes in the portal:

    python3 Tools/generate-icon.py

The mark (`frontend/public/helmsley-h.svg`) is 825.4 x 1080.8 — taller than it is wide — and every
rasteriser to hand fits an SVG to its output box, so handing one the mark directly stretches it into
a square. Instead the whole icon is composed as a square SVG, tile and centred mark together, and
rendered once: the viewBox is already square, so nothing is distorted and there is no compositing
step to get wrong.

The tile carries the brand's two colours as a gradient and the mark is knocked out of it in white.
The mark's own two-tone colouring is lost that way, but the alternative — the mark on white — is a
tile that cannot be seen at all against a light Dock or a Finder sidebar, which is where this icon
spends its whole life.
"""
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MARK = Path("/Users/ben/Documents/Projects/Helmsley/frontend/public/helmsley-h.svg")

# Both targets get one: the Dock and the About box read the app's, and the Finder sidebar entry for
# a mounted domain is the extension's.
CATALOGS = [ROOT / "HelmsleyDrive" / "Assets.xcassets", ROOT / "FileProvider" / "Assets.xcassets"]

# macOS icon geometry: a 1024 canvas with the tile inset 100 a side, corner radius 185.4. Apple's
# own icons sit in that box; filling the canvas edge to edge makes an icon that looks oversized
# beside every other one in the Dock.
CANVAS, INSET, RADIUS = 1024, 100, 185.4
TILE = CANVAS - 2 * INSET

# The mark's own coordinate system, from its viewBox, and how tall it stands within the tile —
# enough to read at 16pt, with room left that the tile still reads as a tile and not as a frame
# jammed against the letter.
MARK_X, MARK_Y, MARK_W, MARK_H = 646.6, 518.3, 825.4, 1080.8
MARK_HEIGHT = 520.0

TEAL, GREEN = "#1cbab0", "#8fb533"

# point size, scale — the full set macOS asks for.
RENDITIONS = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)]


def compose():
    """The complete icon as one square SVG."""
    svg = MARK.read_text()
    paths = re.findall(r"<path[^>]*/>", svg)
    if len(paths) != 2:
        sys.exit(f"expected 2 paths in {MARK}, found {len(paths)}")

    scale = MARK_HEIGHT / MARK_H
    dx = (CANVAS - MARK_W * scale) / 2
    dy = (CANVAS - MARK_HEIGHT) / 2

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" width="{CANVAS}" height="{CANVAS}">
  <defs><linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="{TEAL}"/><stop offset="1" stop-color="{GREEN}"/>
  </linearGradient></defs>
  <style>.a{{fill:#ffffff}}.b{{fill:#ffffff}}</style>
  <rect x="{INSET}" y="{INSET}" width="{TILE}" height="{TILE}" rx="{RADIUS}" ry="{RADIUS}" fill="url(#tile)"/>
  <g transform="translate({dx:.3f},{dy:.3f}) scale({scale:.6f}) translate({-MARK_X},{-MARK_Y})">
    {chr(10) + "    " + (chr(10) + "    ").join(paths)}
  </g>
</svg>
'''


def render_master(work):
    """The 1024 master. Quick Look is the only rasteriser macOS ships; a square viewBox is what
    makes it safe to use, since fitting a square to a square distorts nothing."""
    source = work / "icon.svg"
    source.write_text(compose())
    subprocess.run(["qlmanage", "-t", "-s", str(CANVAS), "-o", str(work), str(source)],
                   check=True, capture_output=True)
    master = work / "icon.svg.png"
    if not master.exists():
        sys.exit("Quick Look produced no thumbnail — cannot rasterise the icon")
    return master


def build_catalog(catalog, master):
    iconset = catalog / "AppIcon.appiconset"
    if catalog.exists():
        shutil.rmtree(catalog)
    iconset.mkdir(parents=True)

    images = []
    for points, scale in RENDITIONS:
        pixels = points * scale
        name = f"icon_{points}x{points}{'@2x' if scale == 2 else ''}.png"
        subprocess.run(["sips", "-Z", str(pixels), str(master), "--out", str(iconset / name)],
                       check=True, capture_output=True)
        images.append({"size": f"{points}x{points}", "idiom": "mac", "filename": name, "scale": f"{scale}x"})

    (iconset / "Contents.json").write_text(json.dumps(
        {"images": images, "info": {"version": 1, "author": "xcode"}}, indent=2) + "\n")
    (catalog / "Contents.json").write_text(json.dumps(
        {"info": {"version": 1, "author": "xcode"}}, indent=2) + "\n")
    print(f"wrote {len(images)} renditions to {iconset.relative_to(ROOT)}")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as tmp:
        master = render_master(Path(tmp))
        for catalog in CATALOGS:
            build_catalog(catalog, master)
