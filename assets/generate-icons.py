#!/usr/bin/env python3
"""Generate app icon rasters (icon-512.png, icon.ico, icon.iconset/) from the
logo geometry. The geometry mirrors assets/icon.svg — keep both in sync.

Requires Pillow. The .icns is produced afterwards with:
    iconutil -c icns assets/icon.iconset -o assets/icon.icns
"""

import math
import os
import numpy as np
from PIL import Image, ImageDraw

BASE = 1024  # master render size
SS = 4       # supersampling factor
S = BASE * SS

# Palette: matches the app's BrandGradientBrush (App.axaml), white glyph.
GRAD_START = (123, 128, 238)  # #7B80EE (top-left)
GRAD_END = (69, 71, 143)      # #45478F (bottom-right)
WHITE = (255, 255, 255, 255)
SIDE_DOT_OPACITY = 0.38


def rounded_badge():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # diagonal gradient (top-left to bottom-right)
    xs, ys = np.meshgrid(np.arange(S), np.arange(S))
    t = (xs + ys) / (2 * (S - 1))
    grad_arr = np.empty((S, S, 4), dtype=np.uint8)
    for i in range(3):
        grad_arr[:, :, i] = np.round(GRAD_START[i] + (GRAD_END[i] - GRAD_START[i]) * t)
    grad_arr[:, :, 3] = 255
    grad = Image.fromarray(grad_arr, "RGBA")

    mask = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(mask)
    r = round(S * 0.225)  # macOS-like corner radius
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=r, fill=255)
    img.paste(grad, (0, 0), mask)

    # subtle top sheen (vertical, fading out by mid-height)
    ty = np.clip(ys[:, :1] / (S * 0.5), 0, 1.0)
    sheen_alpha = np.round(255 * 0.16 * (1 - ty)).astype(np.uint8)
    sheen_arr = np.zeros((S, S, 4), dtype=np.uint8)
    sheen_arr[:, :, 0:3] = 255
    sheen_arr[:, :, 3:4] = sheen_alpha
    sheen = Image.fromarray(sheen_arr, "RGBA")
    sheen_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sheen_layer.paste(sheen, (0, 0), mask)
    img = Image.alpha_composite(img, sheen_layer)
    return img


def draw_glyph(img):
    """T-Pad glyph: dial-pad dots with the top row + center column forming a
    "T" at full white; the remaining side dots dimmed. No bottom bar."""
    gap = S * 0.155               # spacing between dot centers
    r = S * 0.058                 # dot radius
    # Four rows spaced by gap, centered on the badge: top row baseline is
    # 1.5*gap above the optical center S/2.
    cx, cy = S * 0.5, S * 0.5 - 1.5 * gap

    full = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    side = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d_full = ImageDraw.Draw(full)
    d_side = ImageDraw.Draw(side)

    def dot(d, row, col):
        x = cx + (col - 1) * gap
        y = cy + row * gap
        d.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)

    # Top row (3 dots) — full white.
    dot(d_full, 0, 0)
    dot(d_full, 0, 1)
    dot(d_full, 0, 2)
    # Center column, rows 1-3 — full white.
    dot(d_full, 1, 1)
    dot(d_full, 2, 1)
    dot(d_full, 3, 1)
    # Remaining side dots, rows 1-3 — dimmed.
    dot(d_side, 1, 0)
    dot(d_side, 1, 2)
    dot(d_side, 2, 0)
    dot(d_side, 2, 2)
    dot(d_side, 3, 0)
    dot(d_side, 3, 2)

    side_alpha = side.split()[3].point(lambda a: round(a * SIDE_DOT_OPACITY))
    side.putalpha(side_alpha)

    img = Image.alpha_composite(img, side)
    img = Image.alpha_composite(img, full)
    return img


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    master = draw_glyph(rounded_badge()).resize((BASE, BASE), Image.LANCZOS)

    master.resize((512, 512), Image.LANCZOS).save(os.path.join(here, "icon-512.png"))

    # Windows .ico (multi-resolution)
    ico_src = master.resize((256, 256), Image.LANCZOS)
    ico_src.save(os.path.join(here, "icon.ico"),
                 sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    # macOS iconset
    iconset = os.path.join(here, "icon.iconset")
    os.makedirs(iconset, exist_ok=True)
    for pts in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            px = pts * scale
            name = f"icon_{pts}x{pts}" + ("@2x" if scale == 2 else "") + ".png"
            master.resize((px, px), Image.LANCZOS).save(os.path.join(iconset, name))

    write_msix_assets(master, os.path.join(here, "..", "installer", "msix", "Assets"))

    print("Wrote icon-512.png, icon.ico, icon.iconset/, installer/msix/Assets/")


def write_msix_assets(master, out_dir):
    """MSIX visual assets for the Microsoft Store package (installer/msix).

    Tile images get transparent padding so the badge does not touch the tile
    edge; the Windows tile background colour is declared in AppxManifest.xml.
    targetsize-* icons are unpadded so they match other taskbar icons.
    """
    os.makedirs(out_dir, exist_ok=True)

    def padded(width, height, fill_ratio):
        canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        side = round(min(width, height) * fill_ratio)
        glyph = master.resize((side, side), Image.LANCZOS)
        canvas.alpha_composite(glyph, ((width - side) // 2, (height - side) // 2))
        return canvas

    tiles = {
        "Square44x44Logo": (44, 44, 0.75),
        "Square71x71Logo": (71, 71, 0.66),
        "Square150x150Logo": (150, 150, 0.66),
        "Square310x310Logo": (310, 310, 0.66),
        "Wide310x150Logo": (310, 150, 0.66),
        "StoreLogo": (50, 50, 1.0),
    }
    for name, (w, h, fill) in tiles.items():
        for scale in (100, 200):
            img = padded(w * scale // 100, h * scale // 100, fill)
            img.save(os.path.join(out_dir, f"{name}.scale-{scale}.png"))
            if scale == 100:
                # Unqualified copy: makeappx validates manifest references
                # against literal file names when no resources.pri is built.
                img.save(os.path.join(out_dir, f"{name}.png"))

    for px in (16, 24, 32, 48, 256):
        icon = master.resize((px, px), Image.LANCZOS)
        icon.save(os.path.join(out_dir, f"Square44x44Logo.targetsize-{px}.png"))
        icon.save(os.path.join(out_dir, f"Square44x44Logo.targetsize-{px}_altform-unplated.png"))
        icon.save(os.path.join(out_dir, f"Square44x44Logo.targetsize-{px}_altform-lightunplated.png"))


if __name__ == "__main__":
    main()
