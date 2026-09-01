#!/usr/bin/env python3
"""Build the app icon from the portal's own H mark, for every target on every platform.

Run after the mark changes in the portal:

    python3 Mac/Tools/generate-icon.py

Both ports are fed from here rather than each keeping its own copy of the artwork: the Apple targets
get asset catalogs, the Windows one an .ico and a PNG of the same tile, all cut from the single 1024
master rendered below. It lives under Mac/Tools/ because the rasterisers it drives — qlmanage, sips —
are macOS's, so this is the machine it can run on.

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
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MARK = Path("/Users/ben/Documents/Projects/Helmsley/frontend/public/helmsley-h.svg")

# Every target gets one: the Dock and the About box read the app's, and the Finder sidebar entry for
# a mounted domain is the extension's.
MAC_CATALOGS = [ROOT / "HelmsleyDrive" / "Assets.xcassets", ROOT / "FileProvider" / "Assets.xcassets"]
IOS_CATALOGS = [ROOT / "HelmsleyDrive-iOS" / "Assets.xcassets", ROOT / "FileProvider-iOS" / "Assets.xcassets"]

# The Windows app, which has no catalog: an .ico for the executable and the sync root, and a plain
# PNG for the window's own header, which draws the mark the way the Mac window draws its app icon.
WINDOWS_APP = ROOT.parent / "Windows" / "App"

# What the Windows shell asks for, in the sizes it asks for them: 16-48 across Explorer's views and
# the title bar, 64-128 for the larger tiles, 256 for the extra-large view and the sync root's entry
# in the navigation pane on a high-DPI display.
ICO_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
WINDOWS_MARK = 256

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


def compose(inset=INSET, radius=RADIUS, mark_height=MARK_HEIGHT):
    """The complete icon as one square SVG.

    macOS draws the tile itself — inset, with rounded corners — because the Dock shows exactly the
    pixels given to it. iOS masks the corners of a full-bleed square, so its icon passes inset 0 and
    radius 0; leaving the macOS geometry in would round the corners twice and inset an already
    inset icon.
    """
    svg = MARK.read_text()
    paths = re.findall(r"<path[^>]*/>", svg)
    if len(paths) != 2:
        sys.exit(f"expected 2 paths in {MARK}, found {len(paths)}")

    tile = CANVAS - 2 * inset
    scale = mark_height / MARK_H
    dx = (CANVAS - MARK_W * scale) / 2
    dy = (CANVAS - mark_height) / 2

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" width="{CANVAS}" height="{CANVAS}">
  <defs><linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="{TEAL}"/><stop offset="1" stop-color="{GREEN}"/>
  </linearGradient></defs>
  <style>.a{{fill:#ffffff}}.b{{fill:#ffffff}}</style>
  <rect x="{inset}" y="{inset}" width="{tile}" height="{tile}" rx="{radius}" ry="{radius}" fill="url(#tile)"/>
  <g transform="translate({dx:.3f},{dy:.3f}) scale({scale:.6f}) translate({-MARK_X},{-MARK_Y})">
    {chr(10) + "    " + (chr(10) + "    ").join(paths)}
  </g>
</svg>
'''


def render_master(work, name, svg):
    """A 1024 master. Quick Look is the only rasteriser macOS ships; a square viewBox is what makes
    it safe to use, since fitting a square to a square distorts nothing."""
    source = work / f"{name}.svg"
    source.write_text(svg)
    subprocess.run(["qlmanage", "-t", "-s", str(CANVAS), "-o", str(work), str(source)],
                   check=True, capture_output=True)
    master = work / f"{name}.svg.png"
    if not master.exists():
        sys.exit("Quick Look produced no thumbnail — cannot rasterise the icon")
    return master


def flatten(master, out):
    """Drop the alpha channel. App Store Connect refuses an icon that carries one at all, opaque or
    not, and every rasteriser here emits RGBA."""
    subprocess.run(["xcrun", "swift", str(ROOT / "Tools" / "flatten-png.swift"), str(master), str(out)],
                   check=True, capture_output=True)
    return out


INFO = {"version": 1, "author": "xcode"}


def start_catalog(catalog):
    if catalog.exists():
        shutil.rmtree(catalog)
    catalog.mkdir(parents=True)
    (catalog / "Contents.json").write_text(json.dumps({"info": INFO}, indent=2) + "\n")


def write_imageset(catalog, name, images):
    imageset = catalog / f"{name}.imageset"
    imageset.mkdir(parents=True, exist_ok=True)
    (imageset / "Contents.json").write_text(json.dumps({"images": images, "info": INFO}, indent=2) + "\n")
    return imageset


def build_mac_catalog(catalog, master):
    start_catalog(catalog)
    iconset = catalog / "AppIcon.appiconset"
    iconset.mkdir()

    images = []
    for points, scale in RENDITIONS:
        name = f"icon_{points}x{points}{'@2x' if scale == 2 else ''}.png"
        subprocess.run(["sips", "-Z", str(points * scale), str(master), "--out", str(iconset / name)],
                       check=True, capture_output=True)
        images.append({"size": f"{points}x{points}", "idiom": "mac", "filename": name, "scale": f"{scale}x"})

    (iconset / "Contents.json").write_text(json.dumps({"images": images, "info": INFO}, indent=2) + "\n")
    print(f"wrote {len(images)} renditions to {iconset.relative_to(ROOT)}")


def build_ios_catalog(catalog, opaque, tile_master):
    """One 1024 icon — Xcode's single-size form, which it derives every other size from — plus the
    rounded tile as an ordinary image, since the app's own header wants to draw its icon and iOS
    offers no way to load an app icon out of the asset catalog at runtime."""
    start_catalog(catalog)
    iconset = catalog / "AppIcon.appiconset"
    iconset.mkdir()
    shutil.copyfile(opaque, iconset / "icon_1024.png")
    (iconset / "Contents.json").write_text(json.dumps({
        "images": [{"filename": "icon_1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"}],
        "info": INFO,
    }, indent=2) + "\n")

    imageset = write_imageset(catalog, "AppMark", [
        {"idiom": "universal", "filename": f"mark{suffix}.png", "scale": f"{scale}x"}
        for scale, suffix in ((1, ""), (2, "@2x"), (3, "@3x"))
    ])
    for scale, suffix in ((1, ""), (2, "@2x"), (3, "@3x")):
        subprocess.run(["sips", "-Z", str(72 * scale), str(tile_master), "--out", str(imageset / f"mark{suffix}.png")],
                       check=True, capture_output=True)
    print(f"wrote 1024 icon + AppMark to {catalog.relative_to(ROOT)}")


def read_png(path):
    """A PNG as (width, height, RGBA rows, top down).

    Only what `sips` writes here — 8-bit RGBA, no interlacing — because the alternative is a
    dependency on Pillow for the sake of the twenty lines below, and every other rasteriser in this
    file is already something macOS ships.
    """
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        sys.exit(f"{path} is not a PNG")

    width = height = None
    pixels = b""
    at = 8
    while at < len(data):
        length, kind = struct.unpack(">I4s", data[at:at + 8])
        body = data[at + 8:at + 8 + length]
        at += 12 + length  # header, body, CRC
        if kind == b"IHDR":
            width, height, depth, colour, compression, filtering, interlace = struct.unpack(">IIBBBBB", body)
            if (depth, colour, compression, filtering, interlace) != (8, 6, 0, 0, 0):
                sys.exit(f"{path}: expected 8-bit RGBA, not interlaced")
        elif kind == b"IDAT":
            pixels += body
        elif kind == b"IEND":
            break

    raw = zlib.decompress(pixels)
    stride = width * 4
    rows, previous = [], bytearray(stride)
    at = 0
    for _ in range(height):
        filtering, line = raw[at], bytearray(raw[at + 1:at + 1 + stride])
        at += 1 + stride
        # The five PNG filters, each undone against the byte four to the left (one pixel) and the
        # same byte on the row above.
        for i in range(stride):
            left = line[i - 4] if i >= 4 else 0
            up = previous[i]
            upleft = previous[i - 4] if i >= 4 else 0
            if filtering == 1: line[i] = (line[i] + left) & 0xFF
            elif filtering == 2: line[i] = (line[i] + up) & 0xFF
            elif filtering == 3: line[i] = (line[i] + (left + up) // 2) & 0xFF
            elif filtering == 4:
                estimate = left + up - upleft
                a, b, c = abs(estimate - left), abs(estimate - up), abs(estimate - upleft)
                line[i] = (line[i] + (left if a <= b and a <= c else up if b <= c else upleft)) & 0xFF
            elif filtering != 0:
                sys.exit(f"{path}: unknown row filter {filtering}")
        rows.append(bytes(line))
        previous = line
    return width, height, rows


def write_png(path, width, height, rows):
    """RGBA out, one unfiltered scanline at a time — the counterpart to read_png, and needed for the
    same reason: nothing macOS ships can put an alpha channel back into a PNG."""
    body = b"".join(b"\x00" + row for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    path.write_bytes(b"\x89PNG\r\n\x1a\n"
                     + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(body, 9))
                     + chunk(b"IEND", b""))


def cut_tile(master, out):
    """The tile lifted off the white it was rendered on.

    Quick Look thumbnails are opaque, so the macOS master carries a white square around its rounded
    tile. The Dock never shows it — macOS masks the icon to the same shape — but Windows shows every
    pixel it is given, and a white square behind the tile is exactly what a dark taskbar would make
    obvious. So the corners are re-cut here from the geometry the tile was drawn to.

    The edge is not simply cleared: a pixel the tile half covers was blended into that white when it
    was drawn, and clearing alpha alone leaves the blend behind as a pale fringe. Recovering the
    colour underneath is the same arithmetic run backwards, against the white that is known to have
    been there.
    """
    width, height, rows = read_png(master)
    centre = width / 2
    # A rounded rectangle's signed distance: the distance to the un-rounded box grown by the corner
    # radius, which is negative inside, and within half a pixel of the edge is the coverage.
    box = (width - 2 * INSET) / 2 - RADIUS

    cut = []
    for y in range(height):
        row = rows[y]
        line = bytearray(row)
        dy = abs(y + 0.5 - centre) - box
        for x in range(width):
            dx = abs(x + 0.5 - centre) - box
            outside = (max(dx, 0.0) ** 2 + max(dy, 0.0) ** 2) ** 0.5 + min(max(dx, dy), 0.0) - RADIUS
            alpha = min(max(0.5 - outside, 0.0), 1.0)
            if alpha >= 1.0:
                continue
            at = x * 4
            if alpha <= 0.0:
                line[at + 3] = 0
                continue
            for channel in range(3):
                value = (row[at + channel] - (1 - alpha) * 255) / alpha
                line[at + channel] = int(round(min(max(value, 0), 255)))
            line[at + 3] = int(round(alpha * 255))
        cut.append(bytes(line))

    write_png(out, width, height, cut)
    return out


def ico_dib(path):
    """One icon image as the DIB the format was built around: a 32-bit bottom-up BGRA bitmap under a
    BITMAPINFOHEADER, followed by the 1-bit AND mask.

    A PNG could be embedded whole instead — Windows has read those since Vista — but the shell only
    reliably does so at 256, and the sizes that matter most here are the small ones: the sync root's
    row in Explorer's navigation pane, and the window's title bar.
    """
    width, height, rows = read_png(path)

    # Bottom-up, and BGRA rather than RGBA: both are what BI_RGB means.
    pixels = bytearray()
    for row in reversed(rows):
        for at in range(0, len(row), 4):
            r, g, b, a = row[at:at + 4]
            pixels += bytes((b, g, r, a))

    # Zeroed, and load-bearing all the same: a 32-bit icon is masked by its alpha channel, but the
    # AND mask has to be there and be the right size or the image is read as half its height.
    mask_stride = ((width + 31) // 32) * 4
    mask = bytes(mask_stride * height)

    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, len(pixels) + len(mask), 0, 0, 0, 0)
    return header + bytes(pixels) + mask


def write_ico(out, images):
    """The container: a directory of entries, then the images themselves. 256 is written as 0 —
    the width and height are single bytes, so the largest size the format can name is 255."""
    directory, offset = b"", 6 + 16 * len(images)
    for size, image in images:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    out.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + directory + b"".join(image for _, image in images))


def build_windows(work, master):
    """The Windows app's two files. The .ico is the executable's — which is also the window's, since
    WPF takes an unset Window.Icon from the running exe — and the sync root registration points the
    shell at the copy of it that lands beside the build output. The PNG is the window header's, at
    one fixed size, because an .ico read through WPF's decoder hands back whichever frame it likes.
    """
    WINDOWS_APP.mkdir(parents=True, exist_ok=True)
    tile = cut_tile(master, work / "windows-master.png")

    images = []
    for size in ICO_SIZES:
        rendition = work / f"windows-{size}.png"
        subprocess.run(["sips", "-Z", str(size), str(tile), "--out", str(rendition)],
                       check=True, capture_output=True)
        images.append((size, ico_dib(rendition)))
    write_ico(WINDOWS_APP / "HelmsleyDrive.ico", images)

    subprocess.run(["sips", "-Z", str(WINDOWS_MARK), str(tile), "--out", str(WINDOWS_APP / "AppMark.png")],
                   check=True, capture_output=True)
    print(f"wrote {len(images)}-size .ico + AppMark.png to {WINDOWS_APP.relative_to(ROOT.parent)}")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        # The macOS tile: inset, rounded, transparent around it.
        tile = render_master(work, "tile", compose())
        for catalog in MAC_CATALOGS:
            build_mac_catalog(catalog, tile)

        # The same tile on Windows: its icons are drawn with their own margin inside the box too, so
        # the inset that keeps this one honest in the Dock keeps it honest in the taskbar.
        build_windows(work, tile)

        # The iOS icon: full bleed, since iOS masks the corners itself, and no alpha.
        full = render_master(work, "full", compose(inset=0, radius=0, mark_height=600.0))
        opaque = flatten(full, work / "full-opaque.png")
        for catalog in IOS_CATALOGS:
            build_ios_catalog(catalog, opaque, tile)
