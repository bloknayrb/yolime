"""Renders yolime's icons into src/Yolime.App/Assets (design "B", icon A, 2026-11-02).

Dev-time only; the app never runs this. The generated .ico files are committed.
Run from the repo root:  uv run --no-project --with pillow==11.3.0 python tools/make-icons.py

Each size is drawn on its own at 8x and filtered down, so small sizes stay sharp: at 24 px and below the level
bars are snapped to whole pixels first. Entries are 32 bpp bitmaps, except 256 px, which is a PNG.
"""

import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

SS = 8  # supersampling factor
OUT = Path(__file__).resolve().parent.parent / "src" / "Yolime.App" / "Assets"

LIME = (0x8B, 0xC3, 0x4A, 255)
BARS = (0x1F, 0x39, 0x06, 255)

APP_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 64, 256]
TRAY_SIZES = [16, 20, 24, 28, 32, 36, 40, 48]  # 100% to 300% scale

# Tray colours per taskbar variant (the Tray board). The app picks "dark" whenever Windows' taskbar is dark,
# including an accent-coloured one (Windows 11 colours the taskbar only in dark mode).
TRAY = {
    "light": {"halo": (0x1A, 0x1A, 0x1A), "idle": (0x36, 0x60, 0x0C), "recording": (0xD1, 0x34, 0x38),
              "paused": (0x6E, 0x6E, 0x6E), "setup": (0xF5, 0xA6, 0x23), "elevated": (0x7E, 0x57, 0xC2)},
    "dark": {"halo": (0xFF, 0xFF, 0xFF), "idle": (0x8B, 0xC3, 0x4A), "recording": (0xE8, 0x50, 0x5A),
             "paused": (0xA0, 0xA0, 0xA0), "setup": (0xF5, 0xA6, 0x23), "elevated": (0xA9, 0x8B, 0xE0)},
}


def canvas(size):
    img = Image.new("RGBA", (size * SS, size * SS), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def finish(img, size):
    return img.resize((size, size), Image.Resampling.LANCZOS)


def bars_geometry(size, box_left, box_top, box_size):
    """Three level bars inside a square, as (x, y, w, h) in output pixels. Snapped to whole pixels when small."""
    k = box_size / 56.0  # the bars were designed inside the 56-unit lime square of the 64-unit icon
    raw = [(14.0, 20.0, 7.0, 16.0), (24.5, 11.0, 7.0, 34.0), (35.0, 17.0, 7.0, 22.0)]
    out = []
    if size <= 24:
        w = max(1, round(7 * k))
        gap = max(1, round(3.5 * k))
        total = 3 * w + 2 * gap
        x0 = box_left + round((box_size - total) / 2)
        mid = box_top + box_size / 2
        for i, (_, _, _, h) in enumerate(raw):
            hh = max(w + 1, round(h * k))
            y = round(mid - hh / 2 + (0 if i != 1 else 0))
            out.append((x0 + i * (w + gap), y, w, hh))
    else:
        for (x, y, w, h) in raw:
            out.append((box_left + x * k, box_top + y * k, w * k, h * k))
    return out


def app_icon(size):
    img, d = canvas(size)
    k = size / 64.0
    inset = 4 * k if size > 24 else max(1, round(4 * k))
    if size <= 20:
        inset = 1 if size >= 16 else 0
    box = size - 2 * inset
    radius = 14 / 56 * box
    d.rounded_rectangle([inset * SS, inset * SS, (inset + box) * SS - 1, (inset + box) * SS - 1], radius=radius * SS, fill=LIME)
    for (x, y, w, h) in bars_geometry(size, inset, inset, box):
        d.rounded_rectangle([x * SS, y * SS, (x + w) * SS - 1, (y + h) * SS - 1], radius=(w / 2) * SS, fill=BARS)
    return finish(img, size)


def tray_icon(state, variant, size):
    c = TRAY[variant]
    img, d = canvas(size)
    u = size / 16.0 * SS  # one viewBox unit in supersampled pixels

    def circle(cx, cy, r, fill=None, outline=None, width=0):
        d.ellipse([(cx - r) * u, (cy - r) * u, (cx + r) * u - 1, (cy + r) * u - 1], fill=fill,
                  outline=outline, width=max(1, round(width * u)) if outline else 0)

    def rrect(x, y, w, h, r, fill):
        d.rounded_rectangle([x * u, y * u, (x + w) * u - 1, (y + h) * u - 1], radius=r * u, fill=fill)

    halo, col = c["halo"] + (255,), c[state] + (255,)
    if state == "idle":
        rrect(1.5, 1.5, 13, 13, 3.5, halo)
        rrect(2.5, 2.5, 11, 11, 2.8, col)
        # The bars are cut out: drawn transparent so the taskbar shows through.
        for (x, y, w, h) in [(5, 6.5, 1.6, 3.5), (7.2, 4.5, 1.6, 7), (9.4, 5.8, 1.6, 4.6)]:
            d.rounded_rectangle([x * u, y * u, (x + w) * u - 1, (y + h) * u - 1], radius=0.8 * u, fill=(0, 0, 0, 0))
    elif state == "recording":
        circle(8, 8, 7, fill=halo)
        circle(8, 8, 6.2, fill=(0, 0, 0, 0))
        circle(8, 8, 6.1, outline=col, width=1.4)
        circle(8, 8, 3.4, fill=halo)
        circle(8, 8, 2.8, fill=col)
    elif state in ("paused", "setup"):
        circle(8, 8, 7.3, outline=halo, width=3.6)
        circle(8, 8, 6.4, outline=col, width=1.8)
        if state == "paused":
            # The gap: a wedge at the top right, cleared through both rings.
            gap = Image.new("L", img.size, 0)
            gd = ImageDraw.Draw(gap)
            gd.pieslice([0, 0, img.size[0] - 1, img.size[1] - 1], start=-75, end=-30, fill=255)
            img.paste((0, 0, 0, 0), mask=gap)
        else:
            circle(8, 8, 2.1, fill=halo)
            circle(8, 8, 1.5, fill=col)
    elif state == "elevated":
        circle(8, 8, 7, fill=halo)
        circle(8, 8, 6, fill=col)
        shield = [(8, 4.2), (11, 5.3), (11, 7.9), (10.4, 9.8), (8, 11.7), (5.6, 9.8), (5, 7.9), (5, 5.3)]
        d.polygon([(x * u, y * u) for (x, y) in shield], fill=halo)
    return finish(img, size)


def dib(im):
    """A 32 bpp icon bitmap: BITMAPINFOHEADER (height doubled for the mask), BGRA rows bottom-up, then an all-clear
    AND mask (alpha does the work; the mask must still be present)."""
    w, h = im.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    px = im.convert("RGBA").load()
    rows = b""
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    return header + rows + bytes(mask_row * h)


def write_ico(path, images):
    """A multi-size .ico: 32 bpp bitmaps up to 64 px (every loader reads them), PNG for 256."""
    blobs = []
    for im in images:
        if im.size[0] >= 256:
            buf = io.BytesIO()
            im.save(buf, format="PNG")
            blobs.append((im.size[0], buf.getvalue()))
        else:
            blobs.append((im.size[0], dib(im)))
    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries, data = b"", b""
    for (size, png) in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    path.write_bytes(header + entries + data)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    write_ico(OUT / "yolime.ico", [app_icon(s) for s in APP_SIZES])
    for variant in TRAY:
        for state in ("idle", "recording", "paused", "setup", "elevated"):
            write_ico(OUT / f"tray-{state}-{variant}.ico", [tray_icon(state, variant, s) for s in TRAY_SIZES])
    print(f"wrote icons to {OUT}")


if __name__ == "__main__":
    main()
