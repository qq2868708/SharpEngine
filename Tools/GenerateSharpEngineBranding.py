"""Build the raster Windows icon from the SharpEngine brand geometry.

The SVG beside the generated files is the editable source of truth. This
script keeps the ICO generation deterministic and emits all taskbar/title-bar
sizes from one supersampled RGBA render.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "Resources" / "Branding"
CANVAS = 1024
SCALE = 4


def xy(points: list[tuple[float, float]]) -> list[tuple[int, int]]:
    return [(round(x * SCALE), round(y * SCALE)) for x, y in points]


def linear_gradient(size: tuple[int, int], left: tuple[int, int, int], right: tuple[int, int, int]) -> Image.Image:
    width, height = size
    image = Image.new("RGBA", size)
    pixels = image.load()
    for x in range(width):
        t = x / max(width - 1, 1)
        color = tuple(round(left[i] * (1 - t) + right[i] * t) for i in range(3)) + (255,)
        for y in range(height):
            pixels[x, y] = color
    return image


def build() -> Image.Image:
    size = CANVAS * SCALE
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (72 * SCALE, 72 * SCALE, 952 * SCALE, 952 * SCALE),
        radius=214 * SCALE,
        fill=(9, 26, 47, 255),
    )
    draw.rounded_rectangle(
        (88 * SCALE, 88 * SCALE, 936 * SCALE, 936 * SCALE),
        radius=198 * SCALE,
        outline=(43, 102, 140, 110),
        width=10 * SCALE,
    )

    ribbon = Image.new("L", (size, size), 0)
    ribbon_draw = ImageDraw.Draw(ribbon)
    ribbon_points = [
        (728, 254), (690, 220), (635, 187), (570, 164), (500, 154),
        (408, 160), (333, 188), (275, 235), (235, 293), (215, 347),
        (210, 388), (224, 434), (252, 474), (300, 511), (360, 538),
        (405, 562), (577, 613), (620, 629), (646, 648), (650, 684),
        (640, 712), (614, 734), (575, 750), (535, 758), (480, 756),
        (426, 744), (374, 719), (319, 658), (224, 752), (260, 787),
        (306, 820), (364, 844), (430, 861), (500, 870), (564, 868),
        (632, 850), (695, 817), (748, 770), (786, 716), (807, 674),
        (814, 640), (803, 594), (780, 554), (742, 521), (690, 493),
        (622, 466), (443, 414), (402, 400), (379, 382), (373, 344),
        (382, 314), (408, 291), (440, 278), (480, 274), (530, 279),
        (579, 293), (624, 315), (674, 354),
    ]
    ribbon_draw.polygon(xy(ribbon_points), fill=255)

    glow = Image.new("RGBA", (size, size), (69, 212, 232, 0))
    glow.putalpha(ribbon.filter(ImageFilter.GaussianBlur(18 * SCALE)))
    image = Image.alpha_composite(image, glow)

    gradient = linear_gradient((size, size), (99, 230, 224), (75, 109, 255))
    gradient.putalpha(ribbon)
    image = Image.alpha_composite(image, gradient)
    draw = ImageDraw.Draw(image)
    draw.line(xy([(254, 772), (318, 706)]), fill=(181, 255, 250, 158), width=14 * SCALE, joint="curve")
    draw.line(xy([(704, 252), (770, 318)]), fill=(181, 255, 250, 158), width=14 * SCALE, joint="curve")

    draw.ellipse((420 * SCALE, 420 * SCALE, 604 * SCALE, 604 * SCALE), fill=(8, 25, 44, 255), outline=(138, 242, 236, 255), width=12 * SCALE)
    draw.polygon(xy([(512, 432), (581, 472), (581, 552), (512, 592), (443, 552), (443, 472)]), fill=(160, 244, 245, 255))
    draw.polygon(xy([(512, 456), (558, 482), (558, 536), (512, 562), (466, 536), (466, 482)]), fill=(18, 58, 91, 255))
    draw.ellipse((498 * SCALE, 495 * SCALE, 526 * SCALE, 523 * SCALE), fill=(232, 255, 253, 255))
    return image.resize((CANVAS, CANVAS), Image.Resampling.LANCZOS)


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    image = build()
    image.save(OUTPUT / "SharpEngine.png", format="PNG", optimize=True)
    image.save(
        OUTPUT / "SharpEngine.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(f"Generated {OUTPUT / 'SharpEngine.ico'}")


if __name__ == "__main__":
    main()
