"""
Renders the pump display outside the game, from the same numbers Meter.cs uses.

    python tools/preview_meter.py

Writes preview/meter.gif and preview/meter_full.png.

WHY THIS EXISTS: the panel is a couple of dozen rectangles positioned in screen fractions to
three decimal places, and the only way to see whether two of them overlap is to look at it.
Looking at it in the game costs a launch, a drive to a station, and a tank that is not full --
several minutes for each thousandth you move something. Here it costs a second.

It is a DRAWING check, not a behaviour one: it mirrors the layout arithmetic and the wave
maths, so it catches things sitting on top of each other, text running out of its panel and
waves that are too big or too small. It knows nothing about whether the mod works.

Keep the numbers below in step with Meter.cs by hand. They are duplicated on purpose -- the
alternative is a shared data file that the game would have to parse at runtime for the sake of
a dev tool, which is a worse trade.
"""

import math
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "preview")
ICONS = os.path.join(ROOT, "data", "icons")

# A 16:9 canvas. The panel is positioned in fractions, so the aspect only changes how wide
# the pixels are, not where anything sits relative to anything else.
SCREEN_W, SCREEN_H = 1920, 1080

# ---- mirrored from Meter.cs -------------------------------------------------
X, TOP, W, H = 0.5, 0.715, 0.25, 0.145
TANK_W, TANK_H = 0.034, 0.088
COLUMNS = 22
BUBBLES = 5


def fx(v):
    return v * SCREEN_W


def fy(v):
    return v * SCREEN_H


# EVERYTHING TRANSLUCENT IS COMPOSITED, NOT DRAWN.
#
# ImageDraw.Draw(img, "RGBA") does NOT alpha-blend onto an RGBA image -- it REPLACES the
# pixel, alpha channel and all. So a rectangle asked for at alpha 38 came out as opaque
# white, and the first render of this panel showed two brilliant white bars down the glass
# that do not exist in the game at all. A preview that invents problems is worse than no
# preview, so every translucent shape goes through alpha_composite instead.


def bar(img, left, top, w, h, colour):
    """Hud.Bar: positioned from its top-left."""
    x0, y0 = int(round(fx(left))), int(round(fy(top)))
    x1, y1 = int(round(fx(left + w))), int(round(fy(top + h)))

    x1 = max(x1, x0 + 1)
    y1 = max(y1, y0 + 1)

    tile = Image.new("RGBA", (x1 - x0, y1 - y0), colour)
    img.alpha_composite(tile, (x0, y0))


def rect(img, cx, cy, w, h, colour):
    """Hud.Rect: positioned from its centre."""
    bar(img, cx - w / 2, cy - h / 2, w, h, colour)


def font_for(scale):
    # GTA font 4 is about 58px tall at scale 1.0 on a 1080-high screen. Approximate, and only
    # needs to be close enough to show whether a line of text fits where it was put.
    px = max(8, int(scale * 58))
    for name in ("arialbd.ttf", "arial.ttf", "segoeui.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def text(img, s, x, y, scale, colour, centre=False):
    """Text, composited for the same reason bar() is."""
    f = font_for(scale)

    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    px, py = fx(x), fy(y)
    if centre:
        px -= d.textbbox((0, 0), s, font=f)[2] / 2

    d.text((px, py), s, font=f, fill=colour)
    img.alpha_composite(layer)


def icon(img, name, cx, cy, scale, tint, rotation=0.0):
    path = os.path.join(ICONS, name)
    if not os.path.isfile(path):
        return

    side = max(2, int(scale * SCREEN_H))
    im = Image.open(path).convert("RGBA").resize((side, side), Image.LANCZOS)

    solid = Image.new("RGBA", im.size, tint[:3] + (255,))
    alpha = im.split()[-1].point(lambda a: int(a * tint[3] / 255))
    solid.putalpha(alpha)

    if rotation:
        solid = solid.rotate(-rotation, resample=Image.BICUBIC, expand=False)

    img.alpha_composite(solid, (int(fx(cx) - side / 2), int(fy(cy) - side / 2)))


def liquid(img, x, y, fraction, full, t):
    if fraction <= 0.0005:
        return

    body = (120, 225, 135, 238) if full else (235, 160, 45, 238)
    crest = (175, 245, 185, 250) if full else (255, 210, 120, 250)

    column_w = TANK_W / COLUMNS
    level = TANK_H * fraction
    surface_y = y + TANK_H - level

    settle = 0.0 if full else min(fraction * 6.0, 1.0) * (1.0 - fraction * 0.55)
    a1, a2 = 0.0016 * settle, 0.0009 * settle

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
        if alpha <= 4:
            continue

        bar(img, x + TANK_W * lane, by, size, size, (255, 240, 200, alpha))


def frame(t, fraction, litres, owed):
    img = Image.new("RGBA", (SCREEN_W, SCREEN_H), (48, 52, 58, 255))
    left = X - W / 2
    full = fraction >= 0.999

    rect(img, X, TOP + H / 2, W, H, (8, 8, 10, 212))
    rect(img, X, TOP + 0.0016, W, 0.0032, (245, 175, 55, 235))

    text(img, "XERO - DAVIS AVENUE", X, TOP + 0.0065, 0.28, (235, 200, 120, 220), True)

    lift = math.sin(t * math.pi * 2 / 1.9) * 0.006 if not full else 0.0
    tilt = math.sin(t * math.pi * 2 / 2.7) * 7.0 if not full else 0.0
    icon(img, "fuel.png", left + 0.036, TOP + 0.0545 + lift, 0.052, (245, 175, 55, 235), tilt)

    if not full:
        p = (t % 1.25) / 1.25
        alpha = int(210 * min(p * 6.0, min((1.0 - p) * 3.5, 1.0)))
        if alpha > 4:
            icon(img, "drop.png", left + 0.036, TOP + 0.070 + p * p * 0.030, 0.016,
                 (245, 185, 70, alpha))

    text(img, "%.1f L" % litres, left + 0.108, TOP + 0.027, 0.60, (245, 175, 55, 240), True)
    text(img, "$%.2f   @ $1.27/L" % owed, left + 0.108, TOP + 0.069, 0.30, (225, 225, 225, 220), True)

    tx, ty = left + 0.198, TOP + 0.019
    bar(img, tx - 0.0022, ty - 0.0022, TANK_W + 0.0044, TANK_H + 0.0044, (60, 60, 66, 210))
    bar(img, tx, ty, TANK_W, TANK_H, (20, 20, 24, 220))
    liquid(img, tx, ty, fraction, full, t)
    bar(img, tx + 0.0026, ty + 0.004, 0.0012, TANK_H - 0.008, (255, 255, 255, 38))
    bar(img, tx + TANK_W - 0.0034, ty + 0.004, 0.0009, TANK_H - 0.008, (255, 255, 255, 22))

    status = "TANK FULL" if full else "TANK   %d%%" % round(fraction * 100)
    text(img, status, X, TOP + 0.114, 0.29,
         (150, 235, 160, 235) if full else (215, 215, 215, 210), True)

    # Crop to the panel with a margin, since the rest is empty desktop.
    pad = 0.02
    box = (int(fx(left - pad)), int(fy(TOP - pad)),
           int(fx(left + W + pad)), int(fy(TOP + H + pad)))
    return img.crop(box).convert("RGB")


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    frames = []
    for i in range(48):
        t = i / 24.0
        f = 0.20 + (i / 48.0) * 0.70          # filling as it plays
        frames.append(frame(t, f, 7.3 + i * 0.9, 9.30 + i * 1.15))

    gif = os.path.join(OUT, "meter.gif")
    frames[0].save(gif, save_all=True, append_images=frames[1:], duration=42, loop=0)
    print("  %s  (%d frames)" % (gif, len(frames)))

    still = os.path.join(OUT, "meter_full.png")
    frame(3.0, 1.0, 65.0, 82.55).save(still)
    print("  %s" % still)


if __name__ == "__main__":
    main()
