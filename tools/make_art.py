"""Draws FrontEndTV's icon and TV banner (Pillow), sharp at any size. Usage: python3 tools/make_art.py

The mark: a white TV (frame and stand) with a violet "F" and a violet dot in its screen, on a deep violet squircle.
Everything is drawn at 4x and reduced with Lanczos, so edges stay smooth. The icon is 512 px and the banner
1280x720 (drawable-nodpi): Android shrinks them cleanly, and they stay sharp on a 4K TV; installer/MakeIcon.java
makes the Windows .ico from the icon.
"""
import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT = os.path.join(ROOT, "app/assets/fonts/InterVariable.ttf")
TOP, BOTTOM = (64, 34, 150), (14, 9, 30)      # squircle: violet at the top left, almost black at the bottom right
VIOLET = (172, 124, 255)                       # the "F" and the dot
WHITE = (255, 255, 255, 255)
SS = 4                                         # supersampling


def gradient(w, h, top, bottom, angle=45):
    """Diagonal two-color gradient (with a soft light at the top left)."""
    a = math.radians(angle)
    dx, dy = math.cos(a), math.sin(a)
    span = w * abs(dx) + h * abs(dy)
    ramp = Image.linear_gradient("L").resize((256, 256))
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        for x in range(w):
            t = (x * dx + y * dy) / span
            t = t * t * (3 - 2 * t)   # smoothstep: no band in the middle
            px[x, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    glow = Image.new("L", (w, h), 0)
    ImageDraw.Draw(glow).ellipse((-w * 0.3, -h * 0.35, w * 0.75, h * 0.6), fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(w * 0.12))
    light = Image.new("RGB", (w, h), (150, 110, 255))
    return Image.composite(light, img, glow)


def font(px, weight=700):
    f = ImageFont.truetype(FONT, px)
    try:
        values = []
        for a in f.get_variation_axes():
            name = a["name"] if isinstance(a["name"], str) else a["name"].decode()
            values.append(weight if "eight" in name else 32 if "ptical" in name else a.get("default", 0))
        f.set_variation_by_axes(values)
    except Exception:
        pass
    return f


def tv(s, ox=0.0, oy=0.0):
    """The TV drawn in an s x s square (at supersampled size): RGBA, transparent around it."""
    layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    stroke = round(s * 0.058)
    x0, y0, x1, y1 = s * 0.19, s * 0.245, s * 0.81, s * 0.665
    d.rounded_rectangle((x0, y0, x1, y1), radius=round(s * 0.085), outline=WHITE, width=stroke)
    d.rounded_rectangle((s * 0.37, s * 0.725, s * 0.63, s * 0.775), radius=round(s * 0.025), fill=WHITE)
    # The "F", centered in the screen (by its ink, not its box), a little left of the middle to leave room for the dot
    f = font(round(s * 0.30), 800)
    box = d.textbbox((0, 0), "F", font=f)
    cx, cy = s * 0.475, (y0 + y1) / 2
    d.text((cx - (box[0] + box[2]) / 2, cy - (box[1] + box[3]) / 2), "F", font=f, fill=VIOLET + (255,))
    r = s * 0.032
    dx, dy = s * 0.665, y0 + stroke + s * 0.06
    d.ellipse((dx - r, dy - r, dx + r, dy + r), fill=VIOLET + (255,))
    return layer


def mark(size, bg=True):
    """The icon: the TV on the squircle (bg) or alone."""
    s = size * SS
    if bg:
        img = gradient(s, s, TOP, BOTTOM).convert("RGBA")
        mask = Image.new("L", (s, s), 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, s - 1, s - 1), radius=round(s * 0.225), fill=255)
        img.putalpha(mask)
        # A thin light edge at the top, as on glass
        edge = Image.new("L", (s, s), 0)
        ImageDraw.Draw(edge).rounded_rectangle((0, 0, s - 1, s - 1), radius=round(s * 0.225), outline=60, width=max(2, s // 160))
        edge = ImageChops.multiply(edge, Image.linear_gradient("L").rotate(180).resize((s, s)))
        img.alpha_composite(Image.merge("RGBA", (*[Image.new("L", (s, s), 255)] * 3, edge)))
    else:
        img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    layer = tv(s)
    if bg:   # a soft shadow under the TV
        shadow = layer.getchannel("A").filter(ImageFilter.GaussianBlur(s * 0.02)).point(lambda v: int(v * 0.35))
        dark = Image.new("RGBA", (s, s), (8, 4, 20, 0))
        dark.putalpha(shadow)
        img.alpha_composite(dark, (0, round(s * 0.012)))
    img.alpha_composite(layer)
    return img.resize((size, size), Image.LANCZOS)


def banner(w=1280, h=720):
    """Android TV banner: the TV, "FrontEndTV" (TV in violet) and "DÉVELOPPÉ PAR FLORIAN"."""
    W, H = w * SS, h * SS
    img = gradient(W, H, TOP, BOTTOM, angle=25).convert("RGBA")
    m = mark(round(h * 0.56), bg=False).resize((round(h * 0.56 * SS),) * 2, Image.LANCZOS)
    d = ImageDraw.Draw(img)
    big, small = font(round(H * 0.15), 750), font(round(H * 0.052), 600)
    b1, b2 = d.textbbox((0, 0), "FrontEnd", font=big), d.textbbox((0, 0), "TV", font=big)
    word = (b1[2] - b1[0]) + (b2[2] - b2[0])
    gap = round(W * 0.012)
    total = m.width + gap + word
    x = (W - total) // 2
    img.alpha_composite(m, (x, (H - m.height) // 2 - round(H * 0.005)))
    tx = x + m.width + gap
    ty = round(H * 0.5) - b1[3] + round(H * 0.035)
    d.text((tx - b1[0], ty), "FrontEnd", font=big, fill=WHITE)
    d.text((tx - b1[0] + (b1[2] - b1[0]) + round(H * 0.004), ty), "TV", font=big, fill=VIOLET + (255,))
    sub = "DÉVELOPPÉ PAR FLORIAN"
    sx, sy = tx + round(H * 0.006), ty + b1[3] + round(H * 0.05)
    for ch in sub:   # letter-spaced
        d.text((sx, sy), ch, font=small, fill=(205, 196, 240, 255))
        sx += d.textlength(ch, font=small) + round(H * 0.008)
    return img.resize((w, h), Image.LANCZOS)


if __name__ == "__main__":
    res = os.path.join(ROOT, "app/res/drawable-nodpi")
    mark(512).save(os.path.join(res, "icon.png"), optimize=True)
    banner().save(os.path.join(res, "banner.png"), optimize=True)
    print("icon.png 512x512, banner.png 1280x720")
