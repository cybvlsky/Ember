"""Rebuilds the README images in docs/ from a built Ember.exe.

    python tools/make-docs.py path\\to\\Ember.exe

Needs Pillow (pip install pillow). Quit Ember first: the UI screenshots need the single-instance lock.
"""
import os, subprocess, sys, tempfile
from PIL import Image, ImageDraw, ImageFilter, ImageFont

exe = os.path.abspath(sys.argv[1])
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
docs = os.path.join(root, "docs")
tmp = tempfile.mkdtemp(prefix="ember-docs-")
os.makedirs(os.path.join(docs, "themes"), exist_ok=True)

subprocess.run([exe, "--render", os.path.join(tmp, "render")], check=True)
subprocess.run([exe, "--no-elevate", "--demo", "--language", "en", "--screenshots", os.path.join(tmp, "shots")], check=True)
render, shots = os.path.join(tmp, "render"), os.path.join(tmp, "shots")

BG_TOP, BG_BOTTOM = (34, 34, 34), (28, 28, 28)
THEMES = ["studio", "ember", "aurora", "clock", "cores", "latte", "mono", "neon", "terminal"]


def gradient(w, h, top, bottom):
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        k = y / max(1, h - 1)
        d.line([(0, y), (w, y)], fill=tuple(round(a + (b - a) * k) for a, b in zip(top, bottom)))
    return img


def rounded(size, radius):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius, fill=255)
    return m


def device(scale):
    """The panel's black bezel, as drawn on Ember's Screen page."""
    w, h, pad = round(540 * scale), round(380 * scale), round(30 * scale)
    body = gradient(w, h, (16, 16, 16), (11, 11, 11)).convert("RGBA")
    # A hairline edge, lit from above like the real glass.
    edge_mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(edge_mask).rounded_rectangle([0, 0, w - 1, h - 1], round(12 * scale), outline=255, width=max(1, round(scale / 2)))
    edge = gradient(w, h, (64, 64, 64), (34, 34, 34)).convert("RGBA")
    edge.putalpha(edge_mask)
    body.alpha_composite(edge)
    body.putalpha(rounded((w, h), round(12 * scale)))
    return body, pad


def stage(screen_size, scale, margin, first_frame):
    """Background, the screen's light on the desk, the device shadow and bezel. Returns the image and where the screen goes."""
    bez, pad = device(scale)
    W, H = bez.width + 2 * margin, bez.height + 2 * margin
    img = gradient(W, H, BG_TOP, BG_BOTTOM).convert("RGBA")
    glow = first_frame.convert("RGB").resize((24, 16), Image.BOX).resize((W, H), Image.BILINEAR).filter(ImageFilter.GaussianBlur(W / 10))
    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).ellipse([W * 0.05, H * 0.0, W * 0.95, H * 1.0], fill=150)
    mask = mask.filter(ImageFilter.GaussianBlur(W / 8))
    img = Image.composite(Image.blend(img.convert("RGB"), glow, 0.6).convert("RGBA"), img, mask)
    shadow = Image.new("RGBA", (W, H))
    ImageDraw.Draw(shadow).rounded_rectangle([margin, margin + 20 * scale, margin + bez.width, margin + bez.height + 20 * scale], 12 * scale, fill=(0, 0, 0, 150))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(23 * scale)))
    img.alpha_composite(bez, (margin, margin))
    return img, (margin + pad, margin + pad)


def frame_at(path, size):
    return Image.open(path).convert("RGB").resize(size, Image.LANCZOS)


# Hero: four seconds of the default theme on the device, as an animated WebP.
scale = 2
size = (480 * scale, 320 * scale)
frames_dir = os.path.join(render, "frames")
names = sorted(os.listdir(frames_dir))
first = frame_at(os.path.join(frames_dir, names[0]), size)
base, at = stage(size, scale, 90, first)
frames = []
for n in names:
    f = base.copy()
    f.paste(frame_at(os.path.join(frames_dir, n), size), at)
    frames.append(f.convert("RGB"))
frames[0].save(os.path.join(docs, "hero.webp"), save_all=True, append_images=frames[1:], duration=33, loop=0, quality=90, method=6)

# Social preview (Settings > General > Social preview on GitHub): 1280x640.
social = gradient(1280, 640, BG_TOP, BG_BOTTOM).convert("RGBA")
dev = frames[0].resize((round(frames[0].width * 600 / frames[0].height), 600), Image.LANCZOS)
social.paste(dev, (1280 - dev.width + 40, 20))
logo = Image.open(os.path.join(render, "logo-512.png")).resize((120, 120), Image.LANCZOS)
social.alpha_composite(logo, (70, 170))
d = ImageDraw.Draw(social)
bold = ImageFont.truetype("seguisb.ttf", 80)
reg = ImageFont.truetype("segoeui.ttf", 28)
d.text((66, 306), "Ember", font=bold, fill=(255, 255, 255))
d.text((74, 410), "for the Turing Smart Screen 3.5\"", font=reg, fill=(197, 197, 197))
d.text((74, 450), "github.com/cybvlsky/Ember", font=reg, fill=(154, 154, 154))
social.convert("RGB").save(os.path.join(docs, "social.png"), optimize=True)

# Every built-in theme in a slim bezel.
for t in THEMES:
    img = frame_at(os.path.join(render, t + ".png"), size)
    pad = 18
    out = Image.new("RGBA", (size[0] + 2 * pad, size[1] + 2 * pad), (12, 12, 12, 255))
    out.paste(img, (pad, pad))
    out.putalpha(rounded(out.size, 16))
    out.save(os.path.join(docs, "themes", t + ".png"), optimize=True)

# What the panel sees next to what the link has to carry.
pair = Image.new("RGBA", (size[0] * 2 + 40, size[1]), (0, 0, 0, 0))
for i, name in enumerate(["ember.png", "ember.heat.png"]):
    img = frame_at(os.path.join(render, name), size).convert("RGBA")
    img.putalpha(rounded(size, 12))
    pair.alpha_composite(img, (i * (size[0] + 40), 0))
pair.save(os.path.join(docs, "bandwidth.png"), optimize=True)

# Smoothness: Ember's sender next to whole frames, over the same simulated link.
cmp = os.path.join(render, "compare")
names = sorted(os.listdir(os.path.join(cmp, "ember")))
label = ImageFont.truetype("seguisb.ttf", 22)
note = ImageFont.truetype("segoeui.ttf", 18)
gap, top, bottom = 32, 24, 92
board = gradient(480 * 2 + gap + 2 * 40, 320 + top + bottom, BG_TOP, BG_BOTTOM)
d = ImageDraw.Draw(board)
for i, text in enumerate(["Ember: only what changed", "The whole frame every time"]):
    x = 40 + i * (480 + gap)
    d.text((x, top + 320 + 18), text, font=label, fill=(255, 255, 255))
    d.text((x, top + 320 + 50), ["Values, graph and seconds move as they happen", "Each frame needs 1.9 s, then the next starts"][i], font=note, fill=(154, 154, 154))
mask = rounded((480, 320), 8)
frames = []
for n in names:
    f = board.copy()
    for i, side in enumerate(["ember", "full"]):
        f.paste(Image.open(os.path.join(cmp, side, n)).convert("RGB"), (40 + i * (480 + gap), top), mask)
    frames.append(f)
frames[0].save(os.path.join(docs, "compare.webp"), save_all=True, append_images=frames[1:], duration=33, loop=0, quality=82, method=6)

# App screens (Claude Code, Spotify) in the default theme.
for t in ["claude", "spotify"]:
    img = frame_at(os.path.join(render, "app-" + t + "-studio.png"), size)
    pad = 18
    out = Image.new("RGBA", (size[0] + 2 * pad, size[1] + 2 * pad), (12, 12, 12, 255))
    out.paste(img, (pad, pad))
    out.putalpha(rounded(out.size, 16))
    out.save(os.path.join(docs, "screen-" + t + ".png"), optimize=True)

# The start-up card and the logo.
frame_at(os.path.join(render, "intro.png"), size).save(os.path.join(docs, "intro.png"), optimize=True)
Image.open(os.path.join(render, "logo-512.png")).resize((256, 256), Image.LANCZOS).save(os.path.join(docs, "logo.png"), optimize=True)

# The app, at 2x, trimmed to a sensible file size.
for name in ["screen", "themes", "editor-selected", "apps"]:
    img = Image.open(os.path.join(shots, name + ".png")).convert("RGB")
    img = img.resize((1800, round(img.height * 1800 / img.width)), Image.LANCZOS)
    img.save(os.path.join(docs, "app-" + name.replace("-selected", "") + ".png"), optimize=True)

for f in sorted(os.listdir(docs)):
    p = os.path.join(docs, f)
    if os.path.isfile(p):
        print(f"{f:24} {os.path.getsize(p) / 1024:8.0f} KB")
