"""Draws the WoR Mod Manager logo (concept C: three stacked gold plates on a dark tile) and writes:
  design/logo.png                     1024 px, transparent
  src/WoRModManager.App/app.ico       16-256 px, for the exe, windows and taskbar
  src/WoRModManager.App/logo.png      128 px, for the top bar
Run from the repo root: python -I design/make_logo.py
"""
from pathlib import Path
import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SS = 4                      # supersampling for smooth edges
N = 1024 * SS


def mask(draw_fn):
    m = Image.new("L", (N, N), 0)
    draw_fn(ImageDraw.Draw(m))
    return m


def rounded(m, r):
    # blur + threshold rounds every corner by about r
    return m.filter(ImageFilter.GaussianBlur(r)).point(lambda v: 255 if v >= 128 else 0)


def diamond(cy, w=300, h=150, cx=512):
    s = SS
    return mask(lambda d: d.polygon([((cx - w) * s, cy * s), (cx * s, (cy - h) * s), ((cx + w) * s, cy * s), (cx * s, (cy + h) * s)], fill=255))


def grow(m, px):
    # dilate by about px: a blur reaches px out at low strength, so a low threshold keeps that halo
    return m.filter(ImageFilter.GaussianBlur(px * SS / 2)).point(lambda v: 255 if v >= 4 else 0) if px else m


def lerp(c0, c1, t):
    return tuple(int(a + (b - a) * t) for a, b in zip(c0, c1))


img = Image.new("RGBA", (N, N), (0, 0, 0, 0))

# tile: rounded square with a soft top-to-bottom charcoal gradient and a faint top edge highlight
tile = mask(lambda d: d.rounded_rectangle([64 * SS, 64 * SS, 960 * SS, 960 * SS], radius=210 * SS, fill=255))
grad = Image.new("RGBA", (N, N))
g = np.linspace(0, 1, N)[:, None]
top, bot = np.array((44, 48, 56)), np.array((20, 22, 26))
grad_arr = (top + (bot - top) * g)[:, None, :].repeat(N, 1)
grad = Image.fromarray(np.dstack([grad_arr, np.full((N, N, 1), 255)]).astype(np.uint8), "RGBA")
img.paste(grad, (0, 0), tile)
rim = ImageChops.subtract(tile, tile.transform(tile.size, Image.AFFINE, (1, 0, 0, 0, 1, 3 * SS)))
img.paste((255, 255, 255, 28), (0, 0), rim)

# plates: a full diamond on top, two chevrons below, each clipped by the plate above plus a gap
GAP = 13
ys = [366, 458, 550]
colours = [(228, 190, 106), (198, 162, 88), (168, 137, 74)]
above = None
plates = []
for cy in ys:
    m = diamond(cy)
    if above is not None:
        m = ImageChops.subtract(m, grow(above, GAP))
    plates.append(rounded(m, 11 * SS))
    above = diamond(cy)

# one soft shadow under the stack
shadow = rounded(diamond(ys[-1] + 18), 6 * SS).filter(ImageFilter.GaussianBlur(26 * SS))
shade = Image.new("RGBA", (N, N), (0, 0, 0, 0))
shade.putalpha(ImageChops.multiply(shadow, tile).point(lambda v: v * 110 // 255))
img.alpha_composite(shade)  # composite, not paste: paste would replace the tile's alpha
for m, c in zip(reversed(plates), reversed(colours)):
    img.paste(c + (255,), (0, 0), m)

logo = img.resize((1024, 1024), Image.LANCZOS)
logo.save(ROOT / "design" / "logo.png")
app = ROOT / "src" / "WoRModManager.App"
logo.save(app / "app.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
logo.resize((128, 128), Image.LANCZOS).save(app / "logo.png")
print("wrote logo.png, app.ico, app logo.png")
