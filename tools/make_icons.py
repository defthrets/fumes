"""
Generates the PNGs that ship in data/icons/.

Run from anywhere:  python tools/make_icons.py

WHITE WITH ALPHA, always. Every icon is drawn as a white silhouette and tinted at runtime
by CustomSprite.Color -- so the amber on the pump display, the red on a warning and any
colour a future screen wants all come from one file. Baking the colour in would mean a new
PNG per shade, and a set of files that drift apart the first time the palette moves.

Supersampled 4x and resized down, because PIL has no antialiased drawing: shapes are drawn
into a canvas four times the size and then downsampled with LANCZOS, which is what makes the
curves clean at the size these are actually drawn.
"""

import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), "data", "icons")

SIZE = 256
SS = 4              # supersample factor
W = SIZE * SS

WHITE = (255, 255, 255, 255)
CLEAR = (255, 255, 255, 0)


def canvas():
    img = Image.new("RGBA", (W, W), CLEAR)
    return img, ImageDraw.Draw(img)


def save(img, name):
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    path = os.path.join(OUT, name)
    img.save(path, "PNG")
    print("  %-16s %d x %d" % (name, SIZE, SIZE))


def s(v):
    """Design-space (0..256) to supersampled pixels."""
    return int(round(v * SS))


def rrect(d, box, radius, fill):
    d.rounded_rectangle([s(box[0]), s(box[1]), s(box[2]), s(box[3])],
                        radius=s(radius), fill=fill)


def fuel_pump():
    """
    The classic forecourt pump: body, display window, filler hose over the shoulder.

    Drawn slightly narrow and tall so it still reads at the size the HUD draws it, which is
    about forty pixels. A wider, more accurate pump turns into a grey square down there.
    """
    img, d = canvas()

    # Body and its plinth.
    rrect(d, (58, 40, 150, 224), 14, WHITE)
    rrect(d, (44, 216, 164, 240), 8, WHITE)

    # Display window, punched out so the body reads as a machine and not a brick.
    rrect(d, (76, 62, 132, 112), 8, CLEAR)

    # Keypad slot, same idea, smaller.
    rrect(d, (76, 128, 132, 142), 5, CLEAR)

    # The hose arm: up the right-hand side, over, and down into a nozzle.
    rrect(d, (150, 96, 178, 112), 8, WHITE)
    rrect(d, (178, 64, 196, 112), 9, WHITE)
    rrect(d, (186, 44, 214, 74), 12, WHITE)

    save(img, "fuel.png")


def fuel_pump_bar():
    """
    The pump again, for INSIDE the gauge bar, where it is drawn about sixteen pixels wide.

    Three things are different and all three are forced by that size.

    NO HOSE ARM, as asked. On the full icon the arm reaches out to x=214 while the body spans
    only 58 to 150 -- so the body, the part that carries the shape, gets barely a third of the
    width and the rest goes to an arm that at sixteen pixels is two grey specks. Dropping it
    is not losing detail, it is refusing to spend most of the readable area on detail nobody
    can read.

    THE BODY FILLS THE CANVAS. Removing the arm alone would not have helped much: the art
    would still sit in the middle of a mostly empty square, and a sprite is sized by its
    canvas, not by the ink in it. Out to the edges, the same sixteen pixels carry about twice
    the pump.

    AND IT IS TALLER THAN IT IS WIDE, three to four, saved at that shape rather than squared
    up. Only the WIDTH is constrained -- the bar is sixteen pixels across and the gauge is two
    hundred tall, so height is free. A pump squashed into a square is a washing machine; the
    same ink at 3:4 is a pump, and it is a third bigger into the bargain.
    """
    w, h = 192, 256

    img = Image.new("RGBA", (w * SS, h * SS), CLEAR)
    d = ImageDraw.Draw(img)

    # Plinth, wider than the body, as the full icon has it.
    rrect(d, (2, 220, 190, 252), 10, WHITE)

    # Body, out to the edges.
    rrect(d, (16, 4, 176, 222), 22, WHITE)

    # Display window and keypad, punched out. Without them it is a rounded rectangle, and a
    # rounded rectangle is not a pump -- they are what little shape survives at this size.
    rrect(d, (42, 32, 150, 116), 14, CLEAR)
    rrect(d, (42, 142, 150, 176), 9, CLEAR)

    _save_exact(img.resize((w, h), Image.LANCZOS), "fuel_bar.png")


def droplet():
    """A fuel drop, for anywhere the pump is too busy a shape to read."""
    img, d = canvas()

    # A teardrop: a circle with a triangle sitting on top of it.
    d.ellipse([s(64), s(112), s(192), s(240)], fill=WHITE)
    d.polygon([(s(128), s(24)), (s(70), s(150)), (s(186), s(150))], fill=WHITE)

    save(img, "drop.png")


# ---------------------------------------------------------------------------
# Rotated glyphs for the upright gauge
# ---------------------------------------------------------------------------
#
# GTA CANNOT ROTATE TEXT. DRAW_TEXT takes no angle and there is no argument that
# gives it one -- but CustomSprite has a Rotation, and a sprite is just a PNG. So
# text that has to run up the side of a vertical bar is not text at all: it is
# rendered here, turned on its side, and drawn as pictures.
#
# One file per character rather than a strip, because CustomSprite has no
# texture-coordinate rectangle -- it draws a whole file or nothing, so a sprite
# sheet could not be indexed into.

GLYPH = 56          # canvas per character, before rotation


def _face(px):
    for name in ("ariblk.ttf", "arialbd.ttf", "arial.ttf", "segoeuib.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def _rotated(text, box_w, box_h, px):
    """Draws text into a box and turns it a quarter turn, so it reads bottom-to-top."""
    img = Image.new("RGBA", (box_w, box_h), CLEAR)
    d = ImageDraw.Draw(img)
    f = _face(px)

    left, top, right, bottom = d.textbbox((0, 0), text, font=f)
    d.text(((box_w - (right - left)) / 2 - left,
            (box_h - (bottom - top)) / 2 - top), text, font=f, fill=WHITE)

    # Anti-clockwise, so the first character sits at the bottom and the eye reads
    # upward -- the way a vertical label on a machine is written.
    return img.rotate(90, expand=True, resample=Image.BICUBIC)


def _save_exact(img, name):
    """Saves without the square resize the icons use; these are already the right shape."""
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    path = os.path.join(OUT, name)
    img.save(path, "PNG")
    print("  %-16s %d x %d" % (name, img.size[0], img.size[1]))


def glyphs():
    for n in range(10):
        _save_exact(_rotated(str(n), GLYPH, GLYPH, 44), "g%d.png" % n)

    _save_exact(_rotated("%", GLYPH, GLYPH, 38), "gpct.png")

    # The word as one picture. Kerning inside a word is not worth four more files,
    # and it only ever says one thing.
    _save_exact(_rotated("FUEL", GLYPH * 4, GLYPH, 40), "label_fuel.png")


def main():
    print("Writing icons to " + OUT)
    fuel_pump()
    fuel_pump_bar()
    droplet()
    glyphs()
    print("Done. Deploy with:  .\\build.ps1 -Deploy -FreshData")


if __name__ == "__main__":
    main()
