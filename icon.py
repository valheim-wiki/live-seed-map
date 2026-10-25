"""Mod icons (Thunderstore 256 x 256; CurseForge 400 x 400): the site's own render of the default world with the
player's arrow and the creature range ring the live map draws. Run from the pack root: python3 mod/icon.py"""
import math
from PIL import Image, ImageDraw, ImageFilter

SIZE, SCALE = 256, 4
sheet = Image.open("site/public/assets/map/default.webp").convert("RGB")
crop = sheet.crop((830, 760, 1190, 1120)).resize((SIZE * SCALE, SIZE * SCALE), Image.LANCZOS)
canvas = crop.filter(ImageFilter.GaussianBlur(SCALE * 0.6))
draw = ImageDraw.Draw(canvas, "RGBA")
c = SIZE * SCALE / 2

# Range ring, dashed in the site gold (--gold #dcb76a), as LiveLayer draws it.
radius, dash = 88 * SCALE, 9
for i in range(0, 360, dash * 2):
    draw.arc((c - radius, c - radius, c + radius, c + radius), i, i + dash, fill=(0xdc, 0xb7, 0x6a, 255), width=4 * SCALE)

# The player's arrow, turned north-east like the harness heading, white on a dark outline like the minimap marker.
def arrow(scale, heading):
    points = [(0, -34), (22, 26), (0, 14), (-22, 26)]
    a = math.radians(heading)
    return [(c + (x * math.cos(a) - y * math.sin(a)) * scale, c + (x * math.sin(a) + y * math.cos(a)) * scale) for x, y in points]
draw.polygon(arrow(SCALE * 1.28, 40), fill=(10, 11, 13, 230))
draw.polygon(arrow(SCALE * 1.0, 40), fill=(245, 242, 232, 255))

for path, size in [("mod/LiveSeedMap/icon.png", 256), ("mod/listing/curseforge-icon.png", 400)]:
    icon = canvas.resize((size, size), Image.LANCZOS)
    edge = Image.new("L", (size, size), 0)
    ImageDraw.Draw(edge).rectangle((0, 0, size - 1, size - 1), outline=255, width=round(3 * size / SIZE))
    icon.paste((10, 11, 13), mask=edge)
    icon.save(path)
    print(path, icon.size)
