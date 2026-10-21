"""Builds the README images in docs/ from the sprite sheet and the app's own UI renders.

Usage:
    DesktopJanitor.exe --snapshots docs/ui      # real bubble + memory list with sample data
    python tools/make_readme_art.py

Writes docs/hero.gif, docs/animations.gif, docs/screenshot-*.png and the comic-style diagrams
docs/diagram-*.png. Everything is drawn here or rendered by the app, so no real desktop ever ends
up in the README. (Diagrams are images rather than Mermaid so they look the same everywhere.)
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "DesktopJanitor" / "Assets"
DOCS = ROOT / "docs"
UI = DOCS / "ui"

FW, FH = 44, 32
WALK, SWEEP, IDLE = (0, 8), (1, 6), (2, 4)

INK = (43, 29, 20)
INK_SOFT = (110, 93, 80)
PAPER = (255, 253, 247)
ACCENT = (46, 150, 132)
SOFT = (239, 232, 218)
SOFT_RED = (246, 214, 210)
SHADOW = (224, 214, 196)
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


def screenshot_setup():
    """The setup window, centred on a drawn desktop like a freshly opened download."""
    s = 2
    ui = Image.open(UI / "setup.png").convert("RGBA")
    width, height = ui.width + 160 * s, ui.height + 48 * s + 60 * s
    img, ground = desktop(width, height, s)
    img = img.convert("RGBA")
    img.alpha_composite(ui, ((width - ui.width) // 2, (ground - ui.height) // 2))
    img.convert("RGB").save(DOCS / "screenshot-setup.png", optimize=True)


# --- Comic-style diagrams (drawn at 2x) -------------------------------------------------------

def wrap(draw, text, fnt, width):
    lines, line = [], ""
    for word in text.split():
        trial = f"{line} {word}".strip()
        if draw.textlength(trial, font=fnt) <= width:
            line = trial
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def card(width, height):
    """A light rounded card, so the diagrams read well on GitHub's light and dark themes."""
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle((0, 0, width - 1, height - 1), 28, fill=TILE + (255,))
    return img


def comic_box(draw, rect, fill=PAPER, radius=20):
    x0, y0, x1, y1 = rect
    draw.rounded_rectangle((x0 + 5, y0 + 6, x1 + 5, y1 + 6), radius, fill=SHADOW)
    draw.rounded_rectangle(rect, radius, fill=fill, outline=INK, width=4)


def arrow(draw, start, end, label=None):
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    back = (end[0] - 14 * math.cos(angle), end[1] - 14 * math.sin(angle))
    draw.line([start, back], fill=INK, width=4)
    draw.polygon([
        end,
        (end[0] - 18 * math.cos(angle) + 10 * math.sin(angle), end[1] - 18 * math.sin(angle) - 10 * math.cos(angle)),
        (end[0] - 18 * math.cos(angle) - 10 * math.sin(angle), end[1] - 18 * math.sin(angle) + 10 * math.cos(angle)),
    ], fill=INK)
    if label:
        mx, my = (start[0] + end[0]) / 2, (start[1] + end[1]) / 2
        vertical = abs(end[0] - start[0]) < abs(end[1] - start[1])
        pos, anchor = ((mx + 14, my), "lm") if vertical else ((mx, my - 12), "mb")
        draw.text(pos, label, fill=INK, font=font(22, bold=True), anchor=anchor)


def mini_bubble(draw, rect, text, tail_x):
    """A small speech bubble whose tail points down at tail_x."""
    x0, y0, x1, y1 = rect
    draw.rounded_rectangle(rect, 16, fill=PAPER, outline=INK, width=4)
    draw.polygon([(tail_x - 12, y1 - 2), (tail_x + 12, y1 - 2), (tail_x - 4, y1 + 18)], fill=PAPER)
    draw.line([(tail_x - 12, y1), (tail_x - 4, y1 + 18), (tail_x + 12, y1)], fill=INK, width=4, joint="curve")
    f = font(22, bold=True)
    lines = wrap(draw, text, f, x1 - x0 - 28)
    y = (y0 + y1) / 2 - (len(lines) * 28) / 2 + 14
    for line in lines:
        draw.text(((x0 + x1) / 2, y), line, fill=INK, font=f, anchor="mm")
        y += 28


def diagram_install(sheet):
    steps = [
        "Builds a self-contained exe",
        "Asks a running janitor to clock off",
        "Copies it to your user Programs folder",
        "Adds a Start menu shortcut and a Settings > Apps entry",
        "Starts his shift",
    ]
    width, height, margin, gap = 1100, 300, 36, 42
    box_w = (width - 2 * margin - (len(steps) - 1) * gap) // len(steps)
    img = card(width, height)
    draw = ImageDraw.Draw(img)
    f = font(22)
    top, bottom = 58, height - 40
    for i, text in enumerate(steps):
        x0 = margin + i * (box_w + gap)
        last = i == len(steps) - 1
        comic_box(draw, (x0, top, x0 + box_w, bottom), fill=(220, 240, 234) if last else PAPER)
        y = top + 34
        if last:
            sprite = frames(sheet, IDLE, 2)[0]
            img.alpha_composite(sprite, (x0 + (box_w - sprite.width) // 2, y - 6))
            draw = ImageDraw.Draw(img)
            y += sprite.height + 4
        for line in wrap(draw, text, f, box_w - 26):
            draw.text((x0 + box_w / 2, y), line, fill=INK, font=f, anchor="ma")
            y += 30
        cx, cy = x0 + 26, top
        draw.ellipse((cx - 22, cy - 22, cx + 22, cy + 22), fill=ACCENT, outline=INK, width=4)
        draw.text((cx, cy), str(i + 1), fill="white", font=font(22, bold=True), anchor="mm")
        if not last:
            mid = (top + bottom) // 2
            arrow(draw, (x0 + box_w + 6, mid), (x0 + box_w + gap - 4, mid))
    img.save(DOCS / "diagram-install.png", optimize=True)


def diagram_decide():
    # (title, explanation, side branch: (label, outcome, colour), label on the arrow down to the next step)
    gates = [
        ("Every 15 seconds", "he looks at every app with a visible window", None, None),
        ("Protected?", "Windows itself, security software, admin apps and installers",
         ("yes", "Never touched", SOFT_RED), "no"),
        ("On your deny list?", "Editors, terminals, database tools, and anything you said Never to",
         ("yes", "Never touched", SOFT_RED), "no"),
        ("Unused for 30 minutes?", "Counted only while you're actually at the PC",
         ("not yet", "Keep watching", SOFT), "yes"),
        ("Using at least 150 MB?", "All of the app's processes added together",
         ("no", "Keep watching", SOFT), "yes"),
    ]
    width, left, box_w, pill_x0, pill_x1 = 1100, 44, 620, 800, 1056
    bubble = Image.open(UI / "bubble-question.png").convert("RGBA")
    bubble = bubble.resize((700, round(bubble.height * 700 / bubble.width)), Image.LANCZOS)

    measure = ImageDraw.Draw(Image.new("RGB", (10, 10)))
    title_f, body_f = font(27, bold=True), font(22)
    layout, y = [], 40
    for title, body, branch, down in gates:
        lines = wrap(measure, body, body_f, box_w - 48)
        h = 30 + 34 + len(lines) * 29 + 22
        layout.append((y, h, title, lines, branch, down))
        y += h + 70
    bubble_y = y - 14
    legend = [("Close it", "closed like clicking X"), ("Trim RAM", "keeps running"),
              ("Not now", "snoozed for an hour"), ("Never", "added to your deny list")]
    height = bubble_y + max(bubble.height, len(legend) * 66) + 40

    img = card(width, height)
    draw = ImageDraw.Draw(img)
    for i, (y0, h, title, lines, branch, down) in enumerate(layout):
        comic_box(draw, (left, y0, left + box_w, y0 + h), fill=(220, 240, 234) if i == 0 else PAPER)
        draw.text((left + 24, y0 + 24), title, fill=INK, font=title_f)
        ty = y0 + 24 + 40
        for line in lines:
            draw.text((left + 24, ty), line, fill=INK_SOFT, font=body_f)
            ty += 29
        if branch:
            label, outcome, colour = branch
            mid = y0 + h // 2
            comic_box(draw, (pill_x0, mid - 34, pill_x1, mid + 34), fill=colour, radius=34)
            draw.text(((pill_x0 + pill_x1) / 2, mid), outcome, fill=INK, font=font(24, bold=True), anchor="mm")
            arrow(draw, (left + box_w + 8, mid), (pill_x0 - 6, mid), label)
        next_top = layout[i + 1][0] if i + 1 < len(layout) else bubble_y + 18
        arrow(draw, (left + 90, y0 + h + 8), (left + 90, next_top - 6), down)

    img.alpha_composite(bubble, (left - 20, bubble_y))
    draw = ImageDraw.Draw(img)
    for i, (button, meaning) in enumerate(legend):
        y0 = bubble_y + 26 + i * 64
        draw.text((pill_x0 - 40, y0), button, fill=INK, font=font(22, bold=True))
        draw.text((pill_x0 - 40, y0 + 28), meaning, fill=INK_SOFT, font=font(20))
    img.save(DOCS / "diagram-decide.png", optimize=True)


def app_window(draw, rect):
    x0, y0, x1, y1 = rect
    draw.rounded_rectangle(rect, 10, fill="white", outline=INK, width=4)
    draw.rounded_rectangle((x0, y0, x1, y0 + 34), 10, fill=SOFT, outline=INK, width=4)
    draw.rectangle((x0 + 4, y0 + 20, x1 - 4, y0 + 30), fill=SOFT)
    draw.line([(x0, y0 + 34), (x1, y0 + 34)], fill=INK, width=4)
    for k in range(3):  # minimise, maximise, close
        cx = x1 - 22 - k * 28
        if k == 0:
            draw.line([(cx - 7, y0 + 11), (cx + 7, y0 + 25)], fill=INK, width=3)
            draw.line([(cx - 7, y0 + 25), (cx + 7, y0 + 11)], fill=INK, width=3)
        elif k == 1:
            draw.rectangle((cx - 7, y0 + 11, cx + 7, y0 + 24), outline=INK, width=3)
        else:
            draw.line([(cx - 7, y0 + 18), (cx + 7, y0 + 18)], fill=INK, width=3)


def save_dialog(draw, rect, highlight_cancel=False):
    x0, y0, x1, y1 = rect
    draw.rounded_rectangle(rect, 12, fill=PAPER, outline=INK, width=4)
    draw.text(((x0 + x1) / 2, y0 + 30), "Save changes?", fill=INK, font=font(22, bold=True), anchor="mm")
    labels = ["Save", "No", "Cancel"]
    gap = 6
    bw = (x1 - x0 - 24 - 2 * gap) // 3
    for k, label in enumerate(labels):
        bx = x0 + 12 + k * (bw + gap)
        hot = highlight_cancel and label == "Cancel"
        draw.rounded_rectangle((bx, y1 - 50, bx + bw, y1 - 16), 8, fill=ACCENT if hot else SOFT, outline=INK, width=3)
        draw.text((bx + bw / 2, y1 - 33), label, fill="white" if hot else INK, font=font(15, bold=True), anchor="mm")
    return labels


def cursor(draw, tip):
    x, y = tip
    shape = [(x, y), (x, y + 42), (x + 11, y + 31), (x + 19, y + 48), (x + 27, y + 44), (x + 19, y + 28), (x + 34, y + 28)]
    draw.polygon(shape, fill="white", outline=INK)
    draw.line(shape + [shape[0]], fill=INK, width=3, joint="curve")


def diagram_close(sheet):
    """A four-panel comic: what happens when an app has unsaved work."""
    panels = [
        "He sends WM_CLOSE, exactly what clicking X does.",
        "The app gets to ask you to save.",
        "You choose Cancel. Your work stays put.",
        "8 seconds later he checks, and leaves it be.",
    ]
    margin, gap, pw, ph = 28, 20, 252, 250
    width = 2 * margin + 4 * pw + 3 * gap
    height = margin + ph + 16 + 3 * 30 + 24
    img = card(width, height)
    draw = ImageDraw.Draw(img)
    janitor = frames(sheet, IDLE, 4)[0]
    for i, caption in enumerate(panels):
        x0 = margin + i * (pw + gap)
        y0 = margin
        comic_box(draw, (x0, y0, x0 + pw, y0 + ph), radius=16)
        if i in (0, 3):
            jx, jy = x0 + 12, y0 + ph - janitor.height - 8
            img.alpha_composite(janitor, (jx, jy))
            draw = ImageDraw.Draw(img)
            text = "Closing time!" if i == 0 else "Still open. I won't force it."
            mini_bubble(draw, (x0 + 18, y0 + 18, x0 + pw - 18, y0 + 96), text, tail_x=jx + round(janitor.width * 0.38))
        elif i == 1:
            app_window(draw, (x0 + 18, y0 + 22, x0 + pw - 18, y0 + ph - 22))
            save_dialog(draw, (x0 + 24, y0 + 86, x0 + pw - 24, y0 + 200))
        else:
            save_dialog(draw, (x0 + 18, y0 + 52, x0 + pw - 18, y0 + 186), highlight_cancel=True)
            cursor(draw, (x0 + pw - 70, y0 + 168))
        ty = y0 + ph + 16
        for line in wrap(draw, caption, font(21), pw - 8):
            draw.text((x0 + pw / 2, ty), line, fill=INK, font=font(21), anchor="ma")
            ty += 30
    img.save(DOCS / "diagram-close.png", optimize=True)


def main():
    sheet = Image.open(ASSETS / "janitor.png").convert("RGBA")
    hero_gif(sheet)
    animations_gif(sheet)
    screenshot(sheet, "bubble-question.png", "screenshot-question.png", width=1100, sprite_x=180)
    screenshot(sheet, "memory-panel.png", "screenshot-memory.png", width=1100, sprite_x=180)
    screenshot_setup()
    diagram_install(sheet)
    diagram_decide()
    diagram_close(sheet)
    for name in ("hero.gif", "animations.gif", "screenshot-question.png", "screenshot-memory.png", "screenshot-setup.png",
                 "diagram-install.png", "diagram-decide.png", "diagram-close.png"):
        path = DOCS / name
        print(f"{name:26} {path.stat().st_size // 1024:5} KB")


if __name__ == "__main__":
    main()
