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
    THE SAME PUMP AS fuel_pump, hose arm and all, cropped to its own ink.

    The body-only version read as a washing machine; this is the drawing that reads as a pump,
    which is the whole point of having a picture there. Every shape below is copied from
    fuel_pump unchanged -- only the canvas is different.

    And that is the part worth doing. The square icon spans x 44 to 214 and y 40 to 240 of a
    256 canvas, so a third of its width and a fifth of its height are empty -- and a sprite is
    sized by its CANVAS, not by the ink in it, so at sixteen pixels wide that margin was eating
    a third of the pump. Trimmed to the drawing, the same art comes out about half again
    larger with nothing removed and nothing redrawn.

    Saved at its own shape rather than squared up: only the WIDTH is constrained in the gauge,
    since the bar is sixteen pixels across and two hundred tall. The Gauge reads the height
    back off the file, so this can be recropped without a number needing changing there.
    """
    # The ink's bounding box in fuel_pump's design space, plus two either side to keep the
    # anti-aliased edge off the boundary.
    ox, oy = 42, 38
    cw, ch = 148, 204

    img = Image.new("RGBA", (cw * SS, ch * SS), CLEAR)
    d = ImageDraw.Draw(img)

    def r(box, radius, fill):
        rrect(d, (box[0] - ox, box[1] - oy, box[2] - ox, box[3] - oy), radius, fill)

    # Body and its plinth.
    r((58, 40, 150, 224), 14, WHITE)
    r((44, 216, 164, 240), 8, WHITE)

    # Display window and keypad, punched out.
    r((76, 62, 132, 112), 8, CLEAR)
    r((76, 128, 132, 142), 5, CLEAR)

    # The hose arm, TUCKED IN. On the full icon it reaches to x=214 against a body that ends
    # at 150 -- two thirds of the body's own width hanging off the side, which is honest at
    # forty pixels and a spindly aerial at sixteen. Same three shapes, same shape of gesture,
    # reaching to 188 instead: enough to say "pump", not enough to be most of the silhouette.
    r((150, 96, 170, 110), 7, WHITE)
    r((170, 68, 186, 110), 8, WHITE)
    r((164, 50, 188, 76), 11, WHITE)

    _save_exact(img.resize((cw, ch), Image.LANCZOS), "fuel_bar.png")


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
    """
    The turned FUEL label.

    THE DIGITS USED TO BE HERE TOO -- g0 to g9 and gpct, one PNG per character, because GTA
    cannot rotate text and a number had to run up the side of a bar too narrow for upright
    letters. The gauge does not do that any more: the reading is plain DRAW_TEXT and the bar
    is wide enough for it. Eleven files nothing loaded went on shipping in every download for
    several versions after the code that read them was deleted.
    """
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
