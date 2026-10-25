"""Nexus page image: logo + name + tagline over an app screenshot. 1920x1080.
Run from the repo root: python -I design/make_banner.py <app-screenshot.png>
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
W, H = 1920, 1080
GOLD, CREAM, MUTED = (217, 183, 106), (236, 232, 222), (139, 143, 151)
FONTS = "C:/Windows/Fonts/"

# background: near-black with a faint warm glow behind the screenshot
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
glow = np.exp(-(((xx - W / 2) / 900) ** 2 + ((yy - 760) / 420) ** 2))[..., None]
bg = np.array((14, 16, 19), np.float32) + glow * np.array((40, 32, 14), np.float32)
img = Image.fromarray(bg.clip(0, 255).astype(np.uint8)).convert("RGBA")
d = ImageDraw.Draw(img)

# header: logo + name on one line, tagline under it, all centred
logo = Image.open(ROOT / "design" / "logo.png").resize((150, 150), Image.LANCZOS)
title_f = ImageFont.truetype(FONTS + "seguisb.ttf", 92)
tag_f = ImageFont.truetype(FONTS + "segoeui.ttf", 36)
feat_f = ImageFont.truetype(FONTS + "segoeui.ttf", 26)
title = "WoR Mod Manager"
tw = d.textlength(title, font=title_f)
x0 = (W - (150 + 32 + tw)) / 2
img.alpha_composite(logo, (int(x0), 52))
d.text((x0 + 150 + 32, 127), title, font=title_f, fill=CREAM, anchor="lm")
tag = "One-click install, updates and switching for War of Rights mods"
d.text((W / 2, 236), tag, font=tag_f, fill=GOLD, anchor="mm")
feats = "Red's mods  ·  Only changed files download  ·  Nexus update alerts  ·  Enable / disable  ·  ReShade presets"
d.text((W / 2, 290), feats, font=feat_f, fill=MUTED, anchor="mm")

# screenshot: header + cards only, rounded corners, soft shadow
shot = Image.open(sys.argv[1]).convert("RGBA").crop((8, 1, 1032, 500))  # trim the window frame, which shows whatever is behind it
sw = 1400
shot = shot.resize((sw, int(shot.height * sw / shot.width)), Image.LANCZOS)
r = 18
m = Image.new("L", shot.size, 0)
ImageDraw.Draw(m).rounded_rectangle([0, 0, shot.width - 1, shot.height - 1], radius=r, fill=255)
shot.putalpha(m)
sx, sy = (W - sw) // 2, 352
sh = Image.new("RGBA", (W, H), (0, 0, 0, 0))
ImageDraw.Draw(sh).rounded_rectangle([sx, sy + 18, sx + sw, sy + shot.height + 18], radius=r, fill=(0, 0, 0, 170))
img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(28)))
img.alpha_composite(shot, (sx, sy))
ImageDraw.Draw(img).rounded_rectangle([sx, sy, sx + sw - 1, sy + shot.height - 1], radius=r, outline=(58, 61, 68), width=2)

img.convert("RGB").save(ROOT / "design" / "nexus-banner.png")
print("wrote design/nexus-banner.png")
