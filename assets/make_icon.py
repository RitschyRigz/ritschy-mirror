"""Erzeugt das RitschyMirror-App-Icon (app.ico) + eine PNG-Vorschau.
Motiv: ein Bildschirm, der auf einen zweiten gespiegelt wird (Screen-Mirror /
Capture-Card). Modern, kraeftig, bei 16px noch lesbar.
"""
from PIL import Image, ImageDraw
import os

S = 1024  # Render-Aufloesung, danach runterskaliert
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


# --- Hintergrund: abgerundetes Quadrat mit Vertikal-Gradient (Indigo -> Cyan) ---
top = (37, 30, 90)      # tiefes Indigo
bot = (6, 150, 190)     # Cyan/Teal
pad = int(S * 0.06)
radius = int(S * 0.22)

# Gradient auf eine eigene Ebene, dann mit Rounded-Rect-Maske ausstanzen
grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
for y in range(S):
    gd.line([(0, y), (S, y)], fill=lerp(top, bot, y / S) + (255,))
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, fill=255)
img.paste(grad, (0, 0), mask)
d = ImageDraw.Draw(img)


def screen(cx, cy, w, h, fill, outline, ow, stand=True):
    """Ein Monitor: abgerundeter Rahmen + Standfuss."""
    x0, y0, x1, y1 = cx - w // 2, cy - h // 2, cx + w // 2, cy + h // 2
    r = int(min(w, h) * 0.14)
    d.rounded_rectangle([x0, y0, x1, y1], radius=r, fill=fill, outline=outline, width=ow)
    if stand:
        sw, sh = int(w * 0.10), int(h * 0.16)
        d.rectangle([cx - sw // 2, y1, cx + sw // 2, y1 + sh], fill=outline)
        fw = int(w * 0.42)
        d.rounded_rectangle([cx - fw // 2, y1 + sh, cx + fw // 2, y1 + sh + int(sh * 0.5)],
                            radius=int(sh * 0.25), fill=outline)


# --- Hinterer Monitor (der "gespiegelte", versetzt nach hinten-links, transparent) ---
back = (255, 255, 255, 70)
screen(int(S * 0.40), int(S * 0.42), int(S * 0.40), int(S * 0.30),
       fill=back, outline=(255, 255, 255, 110), ow=int(S * 0.012), stand=False)

# --- Vorderer Monitor (Hauptbildschirm, weiss, kraeftig) ---
white = (245, 250, 255, 255)
ink = (15, 32, 55, 255)
screen(int(S * 0.585), int(S * 0.55), int(S * 0.44), int(S * 0.33),
       fill=white, outline=ink, ow=int(S * 0.018), stand=True)

# --- "Spiegel"-Pfeil: zwei Chevrons vom hinteren zum vorderen Screen ---
ax, ay = int(S * 0.55), int(S * 0.55)
acol = (6, 150, 190, 255)
for off in (0, int(S * 0.055)):
    d.line([(ax - int(S * 0.05) + off, ay - int(S * 0.06)),
            (ax + int(S * 0.005) + off, ay),
            (ax - int(S * 0.05) + off, ay + int(S * 0.06))],
           fill=acol, width=int(S * 0.022), joint="curve")

os.makedirs(os.path.dirname(__file__), exist_ok=True)
preview = os.path.join(os.path.dirname(__file__), "icon_preview.png")
img.resize((256, 256), Image.LANCZOS).save(preview)

ico_path = os.path.join(os.path.dirname(__file__), "app.ico")
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
img.save(ico_path, format="ICO", sizes=sizes)
print("wrote", ico_path)
print("wrote", preview)
