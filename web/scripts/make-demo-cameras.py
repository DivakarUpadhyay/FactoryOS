"""Draws one labelled illustration per demo machine for the demo factory's cameras (web/public/demo-cameras/<code>.jpg).

They are clearly marked "demo camera image"; a real plant points each camera at its IP camera's snapshot address.
usage (from web/): python scripts/make-demo-cameras.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "public", "demo-cameras")
W, H = 960, 540

# code, name, body colour, shape
MACHINES = [
    ("demo-cnc-01", "5-axis CNC 1", (37, 99, 135), "cnc"),
    ("demo-cnc-02", "5-axis CNC 2", (37, 99, 135), "cnc"),
    ("demo-cnc-03", "Turning centre 3", (70, 110, 150), "cnc"),
    ("demo-cnc-04", "Turning centre 4", (70, 110, 150), "cnc"),
    ("demo-press-01", "Stamping press 200 t", (31, 105, 140), "press"),
    ("demo-inj-01", "Injection moulder 180 t", (40, 120, 90), "moulder"),
    ("demo-inj-02", "Injection moulder 250 t", (40, 120, 90), "moulder"),
    ("demo-inj-03", "Injection moulder 350 t", (40, 120, 90), "moulder"),
    ("demo-ac-01", "Air compressor 1", (120, 120, 125), "compressor"),
    ("demo-ac-02", "Air compressor 2", (120, 120, 125), "compressor"),
    ("demo-ch-01", "Chiller 1", (60, 130, 170), "chiller"),
    ("demo-bl-01", "Steam boiler", (150, 70, 50), "boiler"),
    ("demo-rb-01", "Machine-tending robot", (230, 150, 30), "robot"),
    ("demo-rb-02", "Sprue picker robot", (230, 150, 30), "robot"),
    ("demo-cb-01", "Deburring cobot", (90, 160, 200), "robot"),
    ("demo-smt-l1-pp", "Pick-and-place 1", (200, 200, 205), "smt"),
    ("demo-smt-l2-pp", "Pick-and-place 2", (200, 200, 205), "smt"),
    ("demo-smt-l1-rf", "Reflow oven 1", (180, 180, 185), "oven"),
    ("demo-smt-l2-rf", "Reflow oven 2", (180, 180, 185), "oven"),
]


def font(size):
    for f in ("consola.ttf", "DejaVuSansMono.ttf", "cour.ttf"):
        try:
            return ImageFont.truetype(f, size)
        except OSError:
            pass
    return ImageFont.load_default()


def shade(c, k):
    return tuple(max(0, min(255, int(v * k))) for v in c)


def draw_machine(d, kind, c):
    floor_y = 410
    if kind in ("cnc", "press", "moulder", "smt", "oven"):
        w = {"cnc": 320, "press": 300, "moulder": 520, "smt": 420, "oven": 560}[kind]
        h = {"cnc": 280, "press": 300, "moulder": 200, "smt": 200, "oven": 150}[kind]
        x0 = (W - w) // 2
        d.rectangle([x0, floor_y - h, x0 + w, floor_y], fill=c)
        d.rectangle([x0 + 30, floor_y - h + 30, x0 + w - 30, floor_y - h + 30 + h // 3], fill=shade(c, 0.75))   # window / guard
        d.rectangle([x0 + 40, floor_y - 70, x0 + w - 40, floor_y - 50], fill=shade(c, 1.5))
        d.rectangle([x0 + w + 20, floor_y - h + 40, x0 + w + 80, floor_y - h + 150], fill=(220, 220, 215))   # control panel
        d.ellipse([x0 + w + 35, floor_y - h + 60, x0 + w + 65, floor_y - h + 90], fill=(210, 40, 40))
        tower_x = x0 + w - 20
    elif kind == "compressor":
        x0 = 330
        d.rounded_rectangle([x0, 200, x0 + 300, floor_y], 12, fill=c)
        for i in range(6):
            d.rectangle([x0 + 30, 230 + i * 25, x0 + 270, 240 + i * 25], fill=shade(c, 0.7))   # vents
        d.ellipse([x0 - 140, 230, x0 - 20, floor_y], fill=(170, 175, 180))   # receiver tank
        tower_x = x0 + 280
    elif kind == "chiller":
        x0 = 200
        d.rectangle([x0, 230, x0 + 560, floor_y], fill=c)
        for i in range(4):
            d.ellipse([x0 + 30 + i * 135, 245, x0 + 120 + i * 135, 335], fill=shade(c, 0.6))   # fans
        d.rectangle([x0 - 40, 360, x0, 375], fill=(90, 90, 95))
        tower_x = x0 + 540
    elif kind == "boiler":
        d.rounded_rectangle([280, 220, 680, floor_y], 40, fill=c)
        d.rectangle([600, 90, 640, 220], fill=(100, 100, 105))   # flue
        d.ellipse([320, 280, 400, 360], fill=(240, 200, 80))   # burner window
        tower_x = 660
    else:   # robot arm
        base_x = 480
        d.rectangle([base_x - 60, floor_y - 40, base_x + 60, floor_y], fill=shade(c, 0.6))
        d.line([base_x, floor_y - 40, base_x - 80, 220], fill=c, width=40)
        d.line([base_x - 80, 220, base_x + 120, 170], fill=c, width=30)
        d.rectangle([base_x + 110, 160, base_x + 150, 220], fill=(80, 80, 85))
        d.rectangle([600, floor_y - 120, 860, floor_y], fill=(120, 120, 125))   # machine it serves
        tower_x = 840
    for i, col in enumerate([(220, 50, 50), (240, 180, 30), (40, 180, 80)]):   # stack light
        d.rectangle([tower_x, 40 + i * 18, tower_x + 24, 56 + i * 18], fill=col)


def main():
    os.makedirs(OUT, exist_ok=True)
    label = font(22)
    for n, (code, name, colour, kind) in enumerate(MACHINES, start=1):
        img = Image.new("RGB", (W, H), (60, 62, 70))
        d = ImageDraw.Draw(img)
        d.rectangle([0, 0, W, 60], fill=(40, 42, 48))
        for i in range(6):
            d.rectangle([40 + i * 160, 10, 130 + i * 160, 40], fill=(230, 230, 210))   # lights
        d.polygon([(0, 360), (W, 360), (W, H), (0, H)], fill=(95, 100, 95))   # floor
        for i in range(-4, 14):
            d.line([i * 90, H, i * 90 + 140, 360], fill=(85, 90, 85), width=2)
        d.rectangle([150, 470, 810, 480], fill=(230, 190, 30))   # walkway line
        draw_machine(d, kind, colour)
        d.rectangle([0, H - 36, W, H], fill=(0, 0, 0))
        d.text((12, H - 31), f"CAM-{n:02d}  {name}  |  demo camera image", fill=(255, 255, 255), font=label)
        img.save(os.path.join(OUT, f"{code}.jpg"), quality=85)
    print(f"{len(MACHINES)} pictures in {os.path.abspath(OUT)}")


if __name__ == "__main__":
    main()
