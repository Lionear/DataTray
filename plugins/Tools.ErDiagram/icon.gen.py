#!/usr/bin/env python3
"""Draw the ER diagram plugin's icon.

Follows the first-party plugin idiom set by plugins/Tools.MsSqlBacpac/icon.png rather than the app
icon's 3D style: a dark navy tile, the white-stroked database cylinder in the same place, and one
coloured object overlapping bottom-right that says what this plugin does. Measured off that icon so the
two sit in the same family — tile #151C2F at 16..495, cylinder x98..414 / y96..416, 12px white strokes.

The object here is three connected boxes: two feeding one, which is the shape the layout engine actually
produces (customers and products into orders). Green, because cyan is reserved for extensibility and
purple for AI access (see the brand notes in SE-209), and amber is already the BACPAC package.

Run from the repo root:  python3 plugins/Tools.ErDiagram/icon.gen.py
"""

from PIL import Image, ImageDraw

SIZE = 512
SS = 4  # supersample; the cylinder ellipses and rounded corners need it

TILE = (21, 28, 47, 255)
CYL_BODY = (47, 61, 92, 255)
CYL_TOP = (91, 156, 255, 255)
WHITE = (255, 255, 255, 255)
NODE = (52, 211, 153, 255)

STROKE = 12


def s(v):
    return v * SS


def main():
    im = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)

    # Tile.
    d.rounded_rectangle([s(16), s(16), s(495), s(495)], radius=s(104), fill=TILE)

    # Database cylinder, same coordinates as the BACPAC icon so the family reads as one.
    left, right = s(98), s(414)
    top, bottom = s(96), s(416)
    ellipse_h = s(56)

    # Body between the top and bottom ellipse centres.
    d.rectangle([left, top + ellipse_h / 2, right, bottom - ellipse_h / 2], fill=CYL_BODY)
    d.ellipse([left, bottom - ellipse_h, right, bottom], fill=CYL_BODY, outline=WHITE, width=s(STROKE))
    d.rectangle([left, top + ellipse_h / 2, right, bottom - ellipse_h / 2], fill=CYL_BODY)

    # Side walls.
    d.line([left, top + ellipse_h / 2, left, bottom - ellipse_h / 2], fill=WHITE, width=s(STROKE))
    d.line([right, top + ellipse_h / 2, right, bottom - ellipse_h / 2], fill=WHITE, width=s(STROKE))

    # Two band separators, at the same rhythm as the reference icon.
    for y in (s(198), s(305)):
        d.arc([left, y - ellipse_h / 2, right, y + ellipse_h / 2], start=0, end=180,
              fill=WHITE, width=s(STROKE))

    # Top face last so it sits above the body.
    d.ellipse([left, top, right, top + ellipse_h], fill=CYL_TOP, outline=WHITE, width=s(STROKE))

    # Two linked tables, stacked at the right so they break the cylinder's silhouette the way the BACPAC
    # icon's package does. Sized and spaced for **20px**, which is what the Plugin Store actually renders
    # (PluginStoreWindow.axaml). Two earlier attempts failed there rather than in principle: three small
    # boxes turned to noise, and a header rule inside each box became mud below 64px while adding nothing
    # at the size that counts. What has to survive is the silhouette — two marks with a gap between them.
    a = (s(292), s(214), s(482), s(322))
    b = (s(292), s(388), s(482), s(486))

    # The relation, thick enough to still be a line and not a smudge when the whole icon is 20px wide.
    cx = (a[0] + a[2]) / 2
    d.line([cx, a[3], cx, b[1]], fill=WHITE, width=s(18))

    for box in (a, b):
        d.rounded_rectangle(list(box), radius=s(18), fill=NODE, outline=WHITE, width=s(STROKE))

    im.resize((SIZE, SIZE), Image.LANCZOS).save("plugins/Tools.ErDiagram/icon.png")
    print("wrote plugins/Tools.ErDiagram/icon.png")


if __name__ == "__main__":
    main()
