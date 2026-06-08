"""Erzeugt die RitschyMirror-Icons:
  app.ico      — Basis (Exe/Fenster)
  app_on.ico   — Basis + grüner Status-Punkt (Mirror läuft)
  app_off.ico  — Basis + roter Status-Punkt (Mirror gestoppt)
+ icon_preview.png. Motiv: ein Bildschirm, der auf einen zweiten gespiegelt wird.
"""
from PIL import Image, ImageDraw
import os

S = 1024  # Render-Auflösung, danach runterskaliert
HERE = os.path.dirname(__file__)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def draw_base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Hintergrund: abgerundetes Quadrat mit Vertikal-Gradient (Indigo -> Cyan)
    top, bot = (37, 30, 90), (6, 150, 190)
    pad, radius = int(S * 0.06), int(S * 0.22)
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(top, bot, y / S) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    def screen(cx, cy, w, h, fill, outline, ow, stand=True):
        x0, y0, x1, y1 = cx - w // 2, cy - h // 2, cx + w // 2, cy + h // 2
        r = int(min(w, h) * 0.14)
        d.rounded_rectangle([x0, y0, x1, y1], radius=r, fill=fill, outline=outline, width=ow)
        if stand:
            sw, sh = int(w * 0.10), int(h * 0.16)
            d.rectangle([cx - sw // 2, y1, cx + sw // 2, y1 + sh], fill=outline)
            fw = int(w * 0.42)
            d.rounded_rectangle([cx - fw // 2, y1 + sh, cx + fw // 2, y1 + sh + int(sh * 0.5)],
                                radius=int(sh * 0.25), fill=outline)

    # hinterer (gespiegelter) Monitor, versetzt + transparent
    screen(int(S * 0.40), int(S * 0.42), int(S * 0.40), int(S * 0.30),
           fill=(255, 255, 255, 70), outline=(255, 255, 255, 110), ow=int(S * 0.012), stand=False)
    # vorderer Hauptmonitor
    screen(int(S * 0.585), int(S * 0.55), int(S * 0.44), int(S * 0.33),
           fill=(245, 250, 255, 255), outline=(15, 32, 55, 255), ow=int(S * 0.018), stand=True)
    # "Spiegel"-Chevrons
    ax, ay, acol = int(S * 0.55), int(S * 0.55), (6, 150, 190, 255)
    for off in (0, int(S * 0.055)):
        d.line([(ax - int(S * 0.05) + off, ay - int(S * 0.06)),
                (ax + int(S * 0.005) + off, ay),
                (ax - int(S * 0.05) + off, ay + int(S * 0.06))],
               fill=acol, width=int(S * 0.022), joint="curve")
    return img


def with_badge(base, color):
    """Status-Punkt unten rechts (kräftig, mit weißem Ring → auch bei 16px lesbar)."""
    img = base.copy()
    d = ImageDraw.Draw(img)
    r = int(S * 0.21)
    cx, cy = int(S * 0.80), int(S * 0.80)
    d.ellipse([cx - r - int(S * 0.03), cy - r - int(S * 0.03), cx + r + int(S * 0.03), cy + r + int(S * 0.03)],
              fill=(255, 255, 255, 255))  # weißer Ring
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color + (255,))
    return img


ICO_SIZES = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
base = draw_base()
base.save(os.path.join(HERE, "app.ico"), format="ICO", sizes=ICO_SIZES)
with_badge(base, (34, 197, 94)).save(os.path.join(HERE, "app_on.ico"), format="ICO", sizes=ICO_SIZES)   # grün
with_badge(base, (239, 68, 68)).save(os.path.join(HERE, "app_off.ico"), format="ICO", sizes=ICO_SIZES)  # rot
base.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "icon_preview.png"))
print("wrote app.ico, app_on.ico, app_off.ico, icon_preview.png")
