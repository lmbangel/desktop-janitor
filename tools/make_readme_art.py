"""Builds the README images in docs/ from the sprite sheet and the app's own UI renders.

Usage:
    DesktopJanitor.exe --snapshots docs/ui      # real bubble + memory list with sample data
    python tools/make_readme_art.py

Writes docs/hero.gif, docs/animations.gif, docs/screenshot-question.png and docs/screenshot-memory.png.
Everything is drawn here or rendered by the app, so no real desktop ever ends up in the README.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "DesktopJanitor" / "Assets"
DOCS = ROOT / "docs"
UI = DOCS / "ui"

FW, FH = 44, 32
WALK, SWEEP, IDLE = (0, 8), (1, 6), (2, 4)

INK = (43, 29, 20)
TASKBAR = (32, 38, 54)
TASKBAR_HI = (52, 60, 80)
DESKTOP_TOP = (221, 232, 251)
DESKTOP_BOTTOM = (186, 206, 241)
TILE = (246, 241, 231)


def font(size, bold=False):
    name = "segoeuib.ttf" if bold else "segoeui.ttf"
    try:
        return ImageFont.truetype(str(Path("C:/Windows/Fonts") / name), size)
    except OSError:
        return ImageFont.load_default()


def frames(sheet, row_count, scale):
    row, count = row_count
    return [
        sheet.crop((i * FW, row * FH, (i + 1) * FW, (row + 1) * FH)).resize((FW * scale, FH * scale), Image.NEAREST)
        for i in range(count)
    ]


def desktop(width, height, s, gradient=True):
    """A calm desktop with a Windows 11-style taskbar. s = scale (1 for GIFs, 2 for screenshots)."""
    bar = 48 * s
    img = Image.new("RGB", (width, height), DESKTOP_TOP)
    if gradient:
        top, bottom = DESKTOP_TOP, DESKTOP_BOTTOM
        draw = ImageDraw.Draw(img)
        for y in range(height - bar):
            t = y / max(1, height - bar - 1)
            draw.line([(0, y), (width, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
        glow = Image.new("L", (width, height), 0)
        ImageDraw.Draw(glow).ellipse((width * 0.55, -height * 0.4, width * 1.25, height * 0.7), fill=90)
        img.paste((255, 255, 255), (0, 0), glow.filter(ImageFilter.GaussianBlur(60 * s)))

    draw = ImageDraw.Draw(img)
    y0 = height - bar
    draw.rectangle((0, y0, width, height), fill=TASKBAR)
    draw.line([(0, y0), (width, y0)], fill=TASKBAR_HI, width=max(1, s))

    # Centred icons: Start, a search pill, then a few app tiles.
    cy = y0 + bar // 2
    tile = 22 * s
    colours = [(86, 156, 232), (240, 186, 70), (94, 190, 140), (230, 110, 90), (150, 120, 220)]
    total = tile + 8 * s + 96 * s + len(colours) * (tile + 12 * s)
    x = (width - total) // 2
    q = tile // 2 - s
    for dx, dy in ((0, 0), (q + 2 * s, 0), (0, q + 2 * s), (q + 2 * s, q + 2 * s)):
        draw.rectangle((x + dx, cy - tile // 2 + dy, x + dx + q, cy - tile // 2 + dy + q), fill=(76, 160, 240))
    x += tile + 8 * s
    draw.rounded_rectangle((x, cy - 13 * s, x + 96 * s, cy + 13 * s), radius=13 * s, fill=TASKBAR_HI)
    draw.text((x + 14 * s, cy), "Search", fill=(200, 205, 215), font=font(11 * s), anchor="lm")
    x += 96 * s + 12 * s
    for colour in colours:
        draw.rounded_rectangle((x, cy - tile // 2, x + tile, cy + tile // 2), radius=5 * s, fill=colour)
        x += tile + 12 * s

    # Clock, and the janitor's own face in the tray.
    draw.text((width - 14 * s, cy - 7 * s), "14:05", fill=(235, 238, 245), font=font(11 * s), anchor="rm")
    draw.text((width - 14 * s, cy + 8 * s), "2026/10/21", fill=(200, 205, 215), font=font(10 * s), anchor="rm")
    face = Image.open(ASSETS / "janitor.ico").convert("RGBA").resize((18 * s, 18 * s), Image.NEAREST)
    img.paste(face, (width - 96 * s, cy - 9 * s), face)
    return img, y0


def palettize(rgb_frames, extra_sources):
    """One shared palette for every frame, so colours don't flicker between frames."""
    source = Image.new("RGB", (64 * len(extra_sources) + rgb_frames[0].width, max(rgb_frames[0].height, 64)))
    source.paste(rgb_frames[0], (0, 0))
    x = rgb_frames[0].width
    for extra in extra_sources:
        source.paste(extra.convert("RGB").resize((64, 64)), (x, 0))
        x += 64
    palette = source.quantize(colors=96, method=Image.Quantize.MEDIANCUT)
    return [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in rgb_frames]


def hero_gif(sheet):
    """He walks in from the left, stops halfway to sweep, and walks off the right edge."""
    scale, width, height = 3, 720, 172
    background, ground = desktop(width, height, 1, gradient=False)
    walk, sweep = frames(sheet, WALK, scale), frames(sheet, SWEEP, scale)
    w = FW * scale

    out, x, t = [], -w, 0
    swept = False
    while x < width:
        if not swept and x >= width // 2 - w // 2:
            for i in range(len(sweep) * 4):
                out.append((sweep[i % len(sweep)], x))
            swept = True
        out.append((walk[t % len(walk)], x))
        x += 6
        t += 1

    rgb = []
    for sprite, sx in out:
        frame = background.copy()
        frame.paste(sprite, (sx, ground - sprite.height), sprite)
        rgb.append(frame)
    gif = palettize(rgb, [sheet])
    gif[0].save(DOCS / "hero.gif", save_all=True, append_images=gif[1:], duration=100, loop=0, disposal=1)


def animations_gif(sheet):
    """Walk, sweep and idle side by side, labelled."""
    scale, pad, label_h = 4, 18, 30
    w, h = FW * scale, FH * scale
    width = 3 * w + 4 * pad
    height = h + 2 * pad + label_h
    walk, sweep, idle = frames(sheet, WALK, scale), frames(sheet, SWEEP, scale), frames(sheet, IDLE, scale)

    base = Image.new("RGB", (width, height), TILE)
    draw = ImageDraw.Draw(base)
    for i, label in enumerate(("walk", "sweep", "idle")):
        cx = pad + i * (w + pad) + w // 2
        draw.text((cx, pad + h + label_h // 2 + 4), label, fill=INK, font=font(16, bold=True), anchor="mm")
        draw.line([(cx - w // 2 + 10, pad + h), (cx + w // 2 - 10, pad + h)], fill=(214, 204, 186), width=2)

    rgb = []
    for t in range(24):  # 24 ticks of 100 ms loop all three cleanly
        frame = base.copy()
        for i, sprite in enumerate((walk[t % 8], sweep[t % 6], idle[(t // 3) % 4])):
            frame.paste(sprite, (pad + i * (w + pad), pad), sprite)
        rgb.append(frame)
    gif = palettize(rgb, [sheet])
    gif[0].save(DOCS / "animations.gif", save_all=True, append_images=gif[1:], duration=100, loop=0, disposal=1)


def screenshot(sheet, ui_name, out_name, width, sprite_x):
    """The app's real UI render, above a drawn desktop, at 2x."""
    s = 2
    ui = Image.open(UI / ui_name).convert("RGBA")
    janitor = frames(sheet, IDLE, 3 * s)[0]
    top_margin = 28 * s
    height = top_margin + ui.height + janitor.height + 48 * s - 18 * s
    img, ground = desktop(width, height, s)
    img = img.convert("RGBA")

    jy = ground - janitor.height
    img.alpha_composite(janitor, (sprite_x, jy))

    # The bubble's tail tip sits 90 px from its left edge and 26 px above its bottom (2x render).
    head_x = sprite_x + round(janitor.width * 0.38)
    bubble_x = head_x - 90
    bubble_y = jy + round(janitor.height * 0.06) - (ui.height - 26)
    img.alpha_composite(ui, (bubble_x, max(0, bubble_y)))
    img.convert("RGB").save(DOCS / out_name, optimize=True)


def main():
    sheet = Image.open(ASSETS / "janitor.png").convert("RGBA")
    hero_gif(sheet)
    animations_gif(sheet)
    screenshot(sheet, "bubble-question.png", "screenshot-question.png", width=1100, sprite_x=180)
    screenshot(sheet, "memory-panel.png", "screenshot-memory.png", width=1100, sprite_x=180)
    for name in ("hero.gif", "animations.gif", "screenshot-question.png", "screenshot-memory.png"):
        path = DOCS / name
        print(f"{name:26} {path.stat().st_size // 1024:5} KB")


if __name__ == "__main__":
    main()
