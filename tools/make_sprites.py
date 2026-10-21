"""Generates the pixel-art janitor sprite sheet and app icon.

Usage:  python tools/make_sprites.py [--preview out.png]

Writes src/DesktopJanitor/Assets/janitor.png (sheet) and janitor.ico.
Sheet layout: 44x32 frames, row 0 = walk (8), row 1 = sweep (6), row 2 = idle (4).
"""
import math
import sys
from pathlib import Path

from PIL import Image

FW, FH = 44, 32
ROWS = {"walk": 8, "sweep": 6, "idle": 4}

OUT = (43, 29, 20, 255)
SKIN = (246, 200, 160, 255)
SKIN_SH = (222, 168, 128, 255)
CAP = (52, 107, 214, 255)
CAP_SH = (36, 78, 165, 255)
CAP_HI = (110, 160, 240, 255)
HAIR = (120, 72, 40, 255)
SUIT = (46, 150, 132, 255)
SUIT_SH = (32, 110, 98, 255)
SUIT_HI = (96, 192, 172, 255)
SHOE = (64, 56, 54, 255)
BADGE = (250, 248, 236, 255)
HANDLE = (168, 110, 56, 255)
BAND = (206, 66, 60, 255)
BRISTLE = (238, 198, 86, 255)
BRISTLE_SH = (204, 152, 52, 255)
EYE = (32, 22, 22, 255)
BLUSH = (242, 146, 140, 255)
DUST = (206, 200, 190)


class Canvas:
    def __init__(self):
        self.img = Image.new("RGBA", (FW, FH), (0, 0, 0, 0))
        self.px = self.img.load()

    def put(self, x, y, c):
        if 0 <= x < FW and 0 <= y < FH:
            self.px[x, y] = c

    def rect(self, x0, y0, x1, y1, c):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.put(x, y, c)

    def line(self, x0, y0, x1, y1, c, width=1):
        steps = max(abs(x1 - x0), abs(y1 - y0), 1)
        for i in range(steps + 1):
            t = i / steps
            x = round(x0 + (x1 - x0) * t)
            y = round(y0 + (y1 - y0) * t)
            for w in range(width):
                self.put(x + w, y, c)

    def outline(self):
        """Dark 1px outline around every opaque pixel (4-neighbour)."""
        src = self.img.copy().load()
        for y in range(FH):
            for x in range(FW):
                if src[x, y][3] != 0:
                    continue
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < FW and 0 <= ny < FH and src[nx, ny][3] == 255:
                        self.px[x, y] = OUT
                        break


def draw_leg(cv, hip_x, hip_y, foot_off, lift, color):
    ground = 30 - lift
    top, bottom = hip_y, ground - 2
    for y in range(top, bottom + 1):
        t = (y - top) / max(bottom - top, 1)
        x0 = hip_x - 1 + round(foot_off * t)
        cv.rect(x0, y, x0 + 2, y, color)
    fx = hip_x - 1 + foot_off
    cv.rect(fx, ground - 1, fx + 2, ground - 1, SHOE)
    cv.rect(fx, ground, fx + 3, ground, SHOE)


def draw_character(cv, back_off, front_off, back_lift, front_lift, dy, blink=False):
    draw_leg(cv, 14, 23 + dy, back_off, back_lift, SUIT_SH)

    # Torso (coveralls)
    cv.rect(12, 14 + dy, 20, 22 + dy, SUIT)
    cv.rect(12, 14 + dy, 12, 22 + dy, SUIT_SH)
    cv.rect(13, 14 + dy, 19, 14 + dy, SUIT_HI)
    cv.rect(12, 21 + dy, 20, 21 + dy, SUIT_SH)
    cv.put(18, 16 + dy, BADGE)
    cv.put(19, 16 + dy, BADGE)

    draw_leg(cv, 18, 23 + dy, front_off, front_lift, SUIT)

    # Head
    cv.rect(12, 7 + dy, 20, 13 + dy, SKIN)
    cv.put(12, 13 + dy, (0, 0, 0, 0))
    cv.put(20, 13 + dy, (0, 0, 0, 0))
    cv.rect(12, 7 + dy, 13, 10 + dy, HAIR)
    cv.put(14, 10 + dy, SKIN_SH)
    cv.put(21, 10 + dy, SKIN)
    cv.put(21, 11 + dy, SKIN_SH)
    if blink:
        cv.put(18, 10 + dy, EYE)
        cv.put(19, 10 + dy, EYE)
    else:
        cv.put(18, 9 + dy, EYE)
        cv.put(18, 10 + dy, EYE)
    cv.put(19, 11 + dy, BLUSH)
    cv.rect(18, 12 + dy, 21, 12 + dy, HAIR)
    cv.put(19, 13 + dy, HAIR)

    # Cap with brim to the front
    cv.rect(13, 3 + dy, 19, 3 + dy, CAP)
    cv.rect(12, 4 + dy, 20, 6 + dy, CAP)
    cv.rect(14, 4 + dy, 16, 4 + dy, CAP_HI)
    cv.rect(12, 6 + dy, 20, 6 + dy, CAP_SH)
    cv.rect(19, 7 + dy, 23, 7 + dy, CAP_SH)


def draw_broom(cv, top, bottom, dy):
    tx, ty = top
    bx, by = bottom
    cv.line(tx, ty + dy, bx, by, HANDLE)
    # Bristle head centred under the handle's foot
    cv.rect(bx - 2, by + 1, bx + 1, by + 1, BAND)
    cv.rect(bx - 3, by + 2, bx + 2, by + 2, BRISTLE)
    cv.rect(bx - 3, by + 3, bx + 3, by + 3, BRISTLE)
    cv.rect(bx - 4, by + 4, bx + 3, by + 4, BRISTLE)
    for x in range(bx - 4, bx + 4, 2):
        cv.put(x, by + 3, BRISTLE_SH)
        cv.put(x + 1, by + 4, BRISTLE_SH)


def point_on(top, bottom, t, dy):
    tx, ty = top
    bx, by = bottom
    return round(tx + (bx - tx) * t), round(ty + dy + (by - ty - dy) * t)


def draw_arms_and_hands(cv, top, bottom, dy):
    upper = point_on(top, bottom, 0.28, dy)
    lower = point_on(top, bottom, 0.55, dy)
    cv.line(17, 15 + dy, lower[0] - 1, lower[1], SUIT, width=2)
    cv.rect(upper[0], upper[1], upper[0] + 1, upper[1] + 1, SKIN)
    cv.rect(lower[0], lower[1], lower[0] + 1, lower[1] + 1, SKIN)


def dust(img, cx, cy, r, alpha):
    px = img.load()
    for y in range(cy - r, cy + r + 1):
        for x in range(cx - r, cx + r + 1):
            if (x - cx) ** 2 + (y - cy) ** 2 <= r * r + 0.5 and 0 <= x < FW and 0 <= y < FH:
                if px[x, y][3] == 0:
                    px[x, y] = (*DUST, alpha)


def frame(back_off, front_off, back_lift, front_lift, dy, push, upright=False, blink=False, puffs=()):
    cv = Canvas()
    draw_character(cv, back_off, front_off, back_lift, front_lift, dy, blink)
    if upright:
        top, bottom = (23, 7), (24, 25)
    else:
        top, bottom = (22 + push // 2, 13), (28 + push, 25)
    draw_broom(cv, top, bottom, dy)
    draw_arms_and_hands(cv, top, bottom, dy)
    cv.outline()
    for p in puffs:
        dust(cv.img, *p)
    return cv.img


def walk_frames():
    front = [3, 2, 0, -2, -3, -2, 0, 2]
    front_lift = [0, 0, 0, 0, 0, 1, 2, 1]
    back_lift = [0, 1, 2, 1, 0, 0, 0, 0]
    bob = [0, 0, -1, 0, 0, 0, -1, 0]
    push = [0, 1, 2, 1, 0, -1, -2, -1]
    frames = []
    for i in range(8):
        puffs = {2: [(35, 29, 1, 170)], 3: [(36, 28, 2, 120)]}.get(i, [])
        frames.append(frame(-front[i], front[i], back_lift[i], front_lift[i], bob[i], push[i], puffs=puffs))
    return frames


def sweep_frames():
    push = [-3, -1, 2, 4, 2, -1]
    puffs = [
        [(40, 24, 3, 70)],
        [(41, 23, 2, 35)],
        [],
        [(37, 29, 2, 220), (40, 30, 1, 180)],
        [(38, 27, 2, 190), (41, 29, 2, 150)],
        [(39, 25, 3, 130), (42, 28, 1, 100)],
    ]
    return [frame(-2, 3, 0, 0, 0, push[i], puffs=puffs[i]) for i in range(6)]


def idle_frames():
    return [frame(-1, 1, 0, 0, 0, 0, upright=True, blink=(i == 2)) for i in range(4)]


def build_sheet():
    rows = [walk_frames(), sweep_frames(), idle_frames()]
    sheet = Image.new("RGBA", (FW * max(ROWS.values()), FH * len(rows)), (0, 0, 0, 0))
    for r, frames in enumerate(rows):
        for c, f in enumerate(frames):
            sheet.paste(f, (c * FW, r * FH))
    return sheet


def build_icon(sheet):
    idle = sheet.crop((0, FH * 2, FW, FH * 3))
    head = idle.crop((9, 1, 26, 18))  # 17x17 around cap + face
    return head.resize((256, 256), Image.NEAREST)


def main():
    root = Path(__file__).resolve().parent.parent
    assets = root / "src" / "DesktopJanitor" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    sheet = build_sheet()
    sheet.save(assets / "janitor.png")
    build_icon(sheet).save(assets / "janitor.ico", sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
    print(f"wrote {assets / 'janitor.png'} and janitor.ico")

    if "--preview" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--preview") + 1])
        bg = Image.new("RGBA", sheet.size, (120, 130, 150, 255))
        bg.alpha_composite(sheet)
        bg.resize((sheet.width * 4, sheet.height * 4), Image.NEAREST).save(out)
        print(f"preview {out}")


if __name__ == "__main__":
    main()
