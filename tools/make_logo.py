"""
Draws the FUMES wordmark for the top of the settings panel.

WHY A PNG AND NOT TEXT. The game has four usable fonts and none of them is a
blackletter -- the nearest, the one the panel already uses for its title, is a
signwriter's script. A wordmark that has to be fraktur has to be a picture.

WHITE, WITH ALPHA, like every other icon in data/icons. The tint is applied at draw
time by Icon.DrawSized, so the file carries shape and nothing else: the same PNG is
amber in the title bar and would be any other colour somewhere else, and the mod's
palette stays in one place instead of being baked into an image.

CROPPED TO ITS OWN INK. A glyph box has bearing above and below the letterforms,
and different at the top than the bottom -- so a logo placed by its file's edges
sits visibly off-centre in a bar. Cropping means the file's height IS the letters'
height and centring it centres what you can see.

Supersampled 4x and reduced, because a fraktur is nothing but fine strokes and
sharp corners, and rendering one straight at final size fills them in.

Font: UnifrakturCook Bold, SIL Open Font License 1.1. See tools/fonts/OFL.txt.
"""

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FONT = os.path.join(HERE, "fonts", "UnifrakturCook-Bold.ttf")
OUT = os.path.join(ROOT, "data", "icons", "logo.png")

CLEAR = (255, 255, 255, 0)
INK = (255, 255, 255, 255)

# Final height in pixels. The panel draws it a few hundredths of the screen tall,
# so this only has to beat that on a big monitor -- past which it is bytes for
# nothing.
HEIGHT = 128
SS = 4


def wordmark(text="Fumes"):
    font = ImageFont.truetype(FONT, HEIGHT * SS)

    # Measured before it is drawn, on a throwaway canvas, because a fraktur's
    # flourishes overhang its advance width and a canvas sized from the advance
    # clips them.
    probe = Image.new("RGBA", (1, 1), CLEAR)
    box = ImageDraw.Draw(probe).textbbox((0, 0), text, font=font)

    pad = HEIGHT * SS // 4
    w = box[2] - box[0] + pad * 2
    h = box[3] - box[1] + pad * 2

    img = Image.new("RGBA", (w, h), CLEAR)
    ImageDraw.Draw(img).text((pad - box[0], pad - box[1]), text, font=font, fill=INK)

    # To the ink, now that there is ink to measure.
    ink = img.getbbox()
    if ink:
        img = img.crop(ink)

    scale = HEIGHT / float(img.height)
    img = img.resize((max(1, int(round(img.width * scale))), HEIGHT), Image.LANCZOS)

    return img


def main():
    if not os.path.exists(FONT):
        raise SystemExit("Font missing: " + FONT)

    img = wordmark()
    img.save(OUT, "PNG")

    print("  logo.png  %dx%d  (%d bytes)" % (img.width, img.height, os.path.getsize(OUT)))
    print("  aspect    %.4f  height over width, which is what Icon.Aspect reads" %
          (img.height / float(img.width)))


if __name__ == "__main__":
    main()
