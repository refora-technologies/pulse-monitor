# Draws the installer's artwork from the brand assets, so it can be rebuilt rather than edited.
#
#     python make-images.py
#
# Writes, beside this file:
#   wizard-<w>x<h>.png   the side panel on the Welcome and Finished pages
#   small-<n>.png        the logo in the top corner of every other page
#
# One file per DPI step Inno Setup documents for the modern wizard (6.6 and later). Setup picks
# whichever best fits the screen, so the artwork is never stretched or blurred. Everything is
# drawn large and scaled down, which keeps the type and the glow smooth at every size.

from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE   = Path(__file__).resolve().parent
ICONS  = HERE.parent / "Icons"
FONTS  = HERE.parent / "Fonts"
LOGO   = Image.open(ICONS / "pulse logo.png").convert("RGBA")

# Pulse's own palette: the panel's near-black and violet, the logo's blue and orange.
INK_TOP    = (11, 10, 20)
INK_BOTTOM = (22, 16, 44)
VIOLET     = (124, 58, 237)
ORANGE     = (255, 106, 61)
BLUE       = (37, 140, 220)
TEXT       = (244, 242, 255)
MUTED      = (169, 164, 206)
FAINT      = (110, 106, 150)

# Image areas from the WizardImageFile and WizardSmallImageFile documentation.
WIZARD_SIZES = [(202, 386), (269, 515), (336, 643), (430, 824), (534, 1022)]
SMALL_SIZES  = [58, 77, 97, 124, 159]


def font(weight, size):
    return ImageFont.truetype(str(FONTS / f"PlusJakartaSans-{weight}.ttf"), size)


def glow(size, centre, radius, colour, alpha):
    """A soft round light, the way the logo glows on the website."""
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x, y = centre
    d.ellipse((x - radius, y - radius, x + radius, y + radius), fill=colour + (alpha,))
    return layer.filter(ImageFilter.GaussianBlur(radius * 0.55))


def centred(draw, y, text, f, fill, width):
    box = draw.textbbox((0, 0), text, font=f)
    draw.text(((width - (box[2] - box[0])) / 2 - box[0], y), text, font=f, fill=fill)


def wizard_master():
    """The side panel, drawn at four times the largest size Setup asks for."""
    W, H = 534 * 4, 1022 * 4
    img = Image.new("RGBA", (W, H))

    # Near-black at the top easing into the panel's violet-black at the bottom.
    px = img.load()
    for y in range(H):
        t = y / (H - 1)
        row = tuple(round(INK_TOP[i] + (INK_BOTTOM[i] - INK_TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(W):
            px[x, y] = row

    logo_w = int(W * 0.56)
    logo_y = int(H * 0.20)
    cx, cy = W // 2, logo_y + logo_w // 2

    # The logo's own colours, lit softly behind it.
    img = Image.alpha_composite(img, glow((W, H), (cx - logo_w // 6, cy + logo_w // 8), logo_w // 2, BLUE, 70))
    img = Image.alpha_composite(img, glow((W, H), (cx + logo_w // 5, cy - logo_w // 6), logo_w // 3, ORANGE, 55))
    img = Image.alpha_composite(img, glow((W, H), (cx, int(H * 0.86)), int(W * 0.7), VIOLET, 45))

    mark = LOGO.resize((logo_w, logo_w), Image.LANCZOS)
    img.alpha_composite(mark, (cx - logo_w // 2, logo_y))

    d = ImageDraw.Draw(img)
    y = logo_y + logo_w + int(H * 0.035)

    # The wordmark, as the website writes it: lower case, with the full stop in violet.
    wf = font("Bold", int(W * 0.17))
    word, dot = "pulse", "."
    wb = d.textbbox((0, 0), word + dot, font=wf)
    x0 = (W - (wb[2] - wb[0])) / 2 - wb[0]
    d.text((x0, y), word, font=wf, fill=TEXT)
    d.text((x0 + d.textlength(word, font=wf), y), dot, font=wf, fill=(196, 181, 253))

    y += int(W * 0.25)
    tf = font("Medium", int(W * 0.068))
    centred(d, y, "Your PC.", tf, MUTED, W)
    centred(d, y + int(W * 0.095), "In plain sight.", tf, MUTED, W)

    bf = font("Medium", int(W * 0.05))
    centred(d, H - int(H * 0.075), "by Refora Technologies", bf, FAINT, W)
    return img


def main():
    master = wizard_master()
    for w, h in WIZARD_SIZES:
        master.resize((w, h), Image.LANCZOS).convert("RGB").save(HERE / f"wizard-{w}x{h}.png", optimize=True)

    # The corner logo keeps its transparency, so it sits on whatever the page header is.
    for n in SMALL_SIZES:
        LOGO.resize((n, n), Image.LANCZOS).save(HERE / f"small-{n}.png", optimize=True)

    print("written:", ", ".join(sorted(p.name for p in HERE.glob("*.png"))))


if __name__ == "__main__":
    main()
