"""
Renders the pump display outside the game, from the same numbers Meter.cs uses.

    python tools/preview_meter.py

Writes preview/meter.gif and preview/meter_full.png.

WHY THIS EXISTS: the panel is a few dozen rectangles positioned in screen fractions to three
decimal places, and the only way to see whether two of them overlap is to look at it. Looking
at it in the game costs a launch, a drive to a station, and a tank that is not full -- several
minutes for every thousandth you move something. Here it costs a second.

It is a DRAWING check, not a behaviour one: it mirrors the layout arithmetic and the animation
maths, so it catches things sitting on top of each other, text running out of its panel, and
motion that is too fast or too subtle. It knows nothing about whether the mod works.

Keep the numbers below in step with Meter.cs by hand. They are duplicated on purpose -- the
alternative is a shared data file the game would have to parse at runtime for the sake of a
dev tool, which is a worse trade.
"""

import math
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "preview")
ICONS = os.path.join(ROOT, "data", "icons")

SCREEN_W, SCREEN_H = 1920, 1080
ASPECT = SCREEN_W / SCREEN_H

# ---- mirrored from Meter.cs -------------------------------------------------
X, TOP, W, H = 0.5, 0.700, 0.300, 0.160
TANK_X, TANK_Y, TANK_W, TANK_H = 0.020, 0.036, 0.042, 0.088
COL_LEFT, COL_RIGHT = 0.078, 0.238
COLUMNS, BUBBLES = 22, 5
BORDER_SEGMENTS = 120


def fx(v):
    return v * SCREEN_W


def fy(v):
    return v * SCREEN_H


# EVERYTHING TRANSLUCENT IS COMPOSITED, NOT DRAWN.
#
# ImageDraw.Draw(img, "RGBA") does NOT alpha-blend onto an RGBA image -- it REPLACES the pixel,
# alpha channel and all. So a rectangle asked for at alpha 38 came out opaque white, and the
# first render of this panel showed two brilliant white bars down the glass that do not exist
# in the game. A preview that invents problems is worse than no preview.


def bar(img, left, top, w, h, colour):
    """Hud.Bar: positioned from its top-left."""
    x0, y0 = int(round(fx(left))), int(round(fy(top)))
    x1, y1 = int(round(fx(left + w))), int(round(fy(top + h)))

    x1, y1 = max(x1, x0 + 1), max(y1, y0 + 1)
    if x0 < 0 or y0 < 0 or x1 > SCREEN_W or y1 > SCREEN_H:
        return

    img.alpha_composite(Image.new("RGBA", (x1 - x0, y1 - y0), colour), (x0, y0))


def rect(img, cx, cy, w, h, colour):
    """Hud.Rect: positioned from its centre."""
    bar(img, cx - w / 2, cy - h / 2, w, h, colour)


# Font 4 is Chalet Comprime Cologne; font 1 is Sign Painter House Script. Neither ships with
# Windows, so the preview stands in Arial and Segoe Script. The SHAPES are wrong and the widths
# only close -- fine for "does it fit, does it overlap", useless for judging letterforms.
FACES = {
    4: ("arialbd.ttf", "arial.ttf", "segoeui.ttf"),
    1: ("segoescb.ttf", "segoesc.ttf", "arialbi.ttf"),
}


def font_for(scale, font=4):
    px = max(8, int(scale * 58))
    for name in FACES.get(font, FACES[4]):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def text_width(s, scale, font=4):
    """Mirrors Hud.Width: how wide a run is as a fraction of the screen."""
    f = font_for(scale, font)
    d = ImageDraw.Draw(Image.new("RGBA", (8, 8)))
    return d.textbbox((0, 0), s, font=f)[2] / SCREEN_W


def text(img, s, x, y, scale, colour, centre=False, right=False, font=4):
    """Text, composited for the same reason bar() is."""
    f = font_for(scale, font)

    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    px, py = fx(x), fy(y)
    w = d.textbbox((0, 0), s, font=f)[2]
    if centre:
        px -= w / 2
    elif right:
        px -= w

    d.text((px, py), s, font=f, fill=colour)
    img.alpha_composite(layer)


def icon(img, name, cx, cy, scale, tint, rotation=0.0):
    path = os.path.join(ICONS, name)
    if not os.path.isfile(path):
        return

    side = max(2, int(scale * SCREEN_H))
    im = Image.open(path).convert("RGBA").resize((side, side), Image.LANCZOS)

    solid = Image.new("RGBA", im.size, tint[:3] + (255,))
    solid.putalpha(im.split()[-1].point(lambda a: int(a * tint[3] / 255)))

    if rotation:
        solid = solid.rotate(-rotation, resample=Image.BICUBIC, expand=False)

    img.alpha_composite(solid, (int(fx(cx) - side / 2), int(fy(cy) - side / 2)))


# ---- the animated border ----------------------------------------------------


def ring(u, chase, halo):
    d = abs(u - chase)
    if d > 0.5:
        d = 1.0 - d
    return max(0.0, 1.0 - d / halo)


def blend(a, b, t):
    t = min(max(t, 0.0), 1.0)
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(4))


def border(img, left, top, t, full):
    thick, chase_seconds, halo = 0.0022, 2.8, 0.10

    dim = (42, 112, 58, 165) if full else (108, 72, 24, 165)
    lit = (165, 250, 180, 255) if full else (255, 220, 140, 255)

    # Solid frame first, in one piece per edge -- a ring of abutting rectangles does not abut
    # once each edge is rounded to whole pixels, and came out as gold blocks with gaps.
    bar(img, left, top, W, thick, dim)
    bar(img, left, top + H - thick, W, thick, dim)
    bar(img, left, top, thick, H, dim)
    bar(img, left + W - thick, top, thick, H, dim)

    wide = W * ASPECT
    perimeter = 2 * (wide + H)
    step = perimeter / BORDER_SEGMENTS
    chase = (t % chase_seconds) / chase_seconds

    for i in range(BORDER_SEGMENTS):
        u = i / BORDER_SEGMENTS
        glow = max(ring(u, chase, halo), ring(u, (chase + 0.5) % 1.0, halo))
        if glow <= 0.02:
            continue

        colour = blend(dim, lit, glow * glow)
        d = u * perimeter
        run = step * 1.15

        if d < wide:
            bar(img, left + (d / wide) * W, top, run / ASPECT, thick, colour)
        elif d < wide + H:
            bar(img, left + W - thick, top + (d - wide), thick, run, colour)
        elif d < 2 * wide + H:
            x = left + W - ((d - wide - H) / wide) * W
            bar(img, x - run / ASPECT, top + H - thick, run / ASPECT, thick, colour)
        else:
            y = top + H - (d - 2 * wide - H)
            bar(img, left, y - run, thick, run, colour)


# ---- the tank ---------------------------------------------------------------


def liquid(img, x, y, fraction, full, t):
    if fraction <= 0.0005:
        return

    body = (120, 225, 135, 238) if full else (235, 160, 45, 238)
    crest = (175, 245, 185, 250) if full else (255, 210, 120, 250)

    column_w = TANK_W / COLUMNS
    level = TANK_H * fraction
    surface_y = y + TANK_H - level

    settle = 0.0 if full else min(fraction * 6.0, 1.0) * (1.0 - fraction * 0.55)
    a1, a2 = 0.0017 * settle, 0.0010 * settle

    for i in range(COLUMNS):
        u = i / (COLUMNS - 1)
        wave = math.sin(t * 3.3 + u * 7.1) * a1 + math.sin(t * 5.1 - u * 11.7) * a2

        top = max(surface_y + wave, y)
        height = y + TANK_H - top
        if height <= 0.0002:
            continue

        bar(img, x + i * column_w, top, column_w + 0.0002, height, body)
        bar(img, x + i * column_w, top, column_w + 0.0002, 0.0016, crest)

    if full or level < 0.012:
        return

    floor = y + TANK_H
    for i in range(BUBBLES):
        lane = 0.16 + (i * 0.68 / (BUBBLES - 1))
        speed = 0.42 + (i % 3) * 0.11
        phase = (t * speed + i * 0.37) % 1.0

        by = floor - level * phase
        size = 0.0016 + (i % 2) * 0.0007

        edge = min(phase * 4.0, min((1.0 - phase) * 3.0, 1.0))
        alpha = int(150 * max(edge, 0.0))
        if alpha > 4:
            bar(img, x + TANK_W * lane, by, size, size, (255, 240, 200, alpha))


# ---- one frame --------------------------------------------------------------


def frame(t, fraction, litres, owed, price=1.27):
    img = Image.new("RGBA", (SCREEN_W, SCREEN_H), (48, 52, 58, 255))
    left = X - W / 2
    full = fraction >= 0.999

    rect(img, X, TOP + H / 2, W, H, (8, 8, 10, 214))

    # Header: brand in script, location in the block font, centred as a unit.
    brand, place = "Xero", "Davis Avenue"
    brand_scale, place_scale, script_lift = 0.42, 0.28, 0.0062
    tail = "  -  " + place.upper()

    bw = text_width(brand, brand_scale, 1)
    tw = text_width(tail, place_scale, 4)
    start = X - (bw + tw) / 2

    text(img, brand, start, TOP + 0.011 - script_lift, brand_scale, (250, 200, 110, 240), font=1)
    text(img, tail, start + bw, TOP + 0.011, place_scale, (220, 205, 175, 210), font=4)

    # Pump icon, top right, with its drip.
    bob = math.sin(t * math.pi * 2 / 1.9) * 0.006 if not full else 0.0
    tilt = math.sin(t * math.pi * 2 / 2.7) * 7.0 if not full else 0.0
    icon(img, "fuel.png", left + W - 0.034, TOP + 0.055 + bob, 0.050, (245, 175, 55, 235), tilt)

    if not full:
        p = (t % 1.25) / 1.25
        alpha = int(210 * min(p * 6.0, min((1.0 - p) * 3.5, 1.0)))
        if alpha > 4:
            icon(img, "drop.png", left + W - 0.034, TOP + 0.070 + p * p * 0.030, 0.016,
                 (245, 185, 70, alpha))

    # Numbers: labels hard left, values hard right.
    lx, rx = left + COL_LEFT, left + COL_RIGHT
    label = (175, 175, 180, 180)
    value = (240, 240, 240, 238)

    text(img, "TOTAL", lx, TOP + 0.048, 0.25, label)
    text(img, "$%.2f" % owed, rx, TOP + 0.036, 0.62, (245, 175, 55, 245), right=True)

    text(img, "VOLUME", lx, TOP + 0.085, 0.25, label)
    text(img, "%.1f L" % litres, rx, TOP + 0.079, 0.38, value, right=True)

    text(img, "PRICE", lx, TOP + 0.112, 0.25, label)
    text(img, "$%.2f/L" % price, rx, TOP + 0.109, 0.30, (215, 215, 218, 215), right=True)

    # Tank.
    tx, ty = left + TANK_X, TOP + TANK_Y
    bar(img, tx - 0.0024, ty - 0.0024, TANK_W + 0.0048, TANK_H + 0.0048, (62, 62, 68, 215))
    bar(img, tx, ty, TANK_W, TANK_H, (20, 20, 24, 224))
    liquid(img, tx, ty, fraction, full, t)
    bar(img, tx + 0.0030, ty + 0.005, 0.0013, TANK_H - 0.010, (255, 255, 255, 38))
    bar(img, tx + TANK_W - 0.0040, ty + 0.005, 0.0010, TANK_H - 0.010, (255, 255, 255, 22))

    status = "FULL" if full else "%d%%" % round(fraction * 100)
    text(img, status, tx + TANK_W / 2, TOP + TANK_Y + TANK_H + 0.004, 0.32,
         (150, 235, 160, 235) if full else (225, 225, 225, 220), centre=True)

    border(img, left, TOP, t, full)

    pad = 0.018
    box = (int(fx(left - pad)), int(fy(TOP - pad)),
           int(fx(left + W + pad)), int(fy(TOP + H + pad)))
    return img.crop(box).convert("RGB")


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    frames = []
    for i in range(60):
        t = i / 24.0
        f = 0.18 + (i / 60.0) * 0.74
        frames.append(frame(t, f, 65.0 * f, 65.0 * f * 1.27))

    gif = os.path.join(OUT, "meter.gif")
    frames[0].save(gif, save_all=True, append_images=frames[1:], duration=42, loop=0)
    print("  %s  (%d frames)" % (gif, len(frames)))

    still = os.path.join(OUT, "meter_full.png")
    frame(3.0, 1.0, 65.0, 82.55).save(still)
    print("  %s" % still)


if __name__ == "__main__":
    main()
