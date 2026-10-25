"""Nexus page header: logo + name + tagline on the left, the app's mod cards on the right. 1300x372.
Run from the repo root: python -I design/make_header.py <my-library-screenshot.png>
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
W, H = 1300, 372
GOLD, CREAM, MUTED = (217, 183, 106), (236, 232, 222), (139, 143, 151)
FONTS = "C:/Windows/Fonts/"

# background: near-black, warm glow behind the cards
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
glow = np.exp(-(((xx - 1000) / 420) ** 2 + ((yy - H / 2) / 260) ** 2))[..., None]
bg = np.array((14, 16, 19), np.float32) + glow * np.array((44, 35, 15), np.float32)
img = Image.fromarray(bg.clip(0, 255).astype(np.uint8)).convert("RGBA")
d = ImageDraw.Draw(img)

# right: the two cards (MAW + Blackpowder) cut from the My Library screenshot
cards = Image.open(sys.argv[1]).convert("RGBA").crop((30, 153, 650, 490))
ch = 290
cards = cards.resize((int(cards.width * ch / cards.height), ch), Image.LANCZOS)
cx, cy = W - cards.width - 34, (H - ch) // 2
m = Image.new("L", cards.size, 0)
ImageDraw.Draw(m).rounded_rectangle([0, 0, cards.width - 1, cards.height - 1], radius=16, fill=255)
cards.putalpha(m)
sh = Image.new("RGBA", (W, H), (0, 0, 0, 0))
ImageDraw.Draw(sh).rounded_rectangle([cx, cy + 12, cx + cards.width, cy + ch + 12], radius=16, fill=(0, 0, 0, 160))
img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(22)))
img.alpha_composite(cards, (cx, cy))

# left: logo, name, tagline, one-line feature list
logo = Image.open(ROOT / "design" / "logo.png").resize((96, 96), Image.LANCZOS)
x0 = 56
img.alpha_composite(logo, (x0, 84))
d.text((x0 + 96 + 20, 132), "WoR Mod Manager", font=ImageFont.truetype(FONTS + "seguisb.ttf", 46), fill=CREAM, anchor="lm")
d.text((x0, 230), "One-click install and updates for", font=ImageFont.truetype(FONTS + "segoeui.ttf", 26), fill=GOLD, anchor="lm")
d.text((x0, 264), "War of Rights mods", font=ImageFont.truetype(FONTS + "segoeui.ttf", 26), fill=GOLD, anchor="lm")
d.text((x0, 312), "Red's mods  ·  Nexus mods  ·  ReShade", font=ImageFont.truetype(FONTS + "segoeui.ttf", 20), fill=MUTED, anchor="lm")

img.convert("RGB").save(ROOT / "design" / "nexus-header.png")
print("wrote design/nexus-header.png")
