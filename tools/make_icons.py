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
from PIL import Image, ImageDraw

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


def droplet():
    """A fuel drop, for anywhere the pump is too busy a shape to read."""
    img, d = canvas()

    # A teardrop: a circle with a triangle sitting on top of it.
    d.ellipse([s(64), s(112), s(192), s(240)], fill=WHITE)
    d.polygon([(s(128), s(24)), (s(70), s(150)), (s(186), s(150))], fill=WHITE)

    save(img, "drop.png")


def main():
    print("Writing icons to " + OUT)
    fuel_pump()
    droplet()
    print("Done. Deploy with:  .\\build.ps1 -Deploy -FreshData")


if __name__ == "__main__":
    main()
