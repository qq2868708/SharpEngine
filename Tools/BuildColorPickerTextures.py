"""Generate small RGBA TGA gradients supported by all RmlUi render backends."""
import colorsys
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[1] / 'Resources/Editor/ColorPicker'
ROOT.mkdir(parents=True, exist_ok=True)


def write_tga(name, width, height, pixel):
    header = struct.pack('<BBBHHBHHHHBB', 0, 0, 2, 0, 0, 0, 0, 0, width, height, 32, 0x28)
    data = bytearray(header)
    for y in range(height):
        for x in range(width):
            r, g, b, a = pixel(x / (width - 1), y / max(1, height - 1))
            data.extend(round(max(0, min(1, value)) * 255) for value in (b, g, r, a))
    (ROOT / name).write_bytes(data)


def sv(saturation, y):
    value = 1 - y
    alpha = 1 - saturation * value
    white = (1 - saturation) * value / alpha if alpha else 0
    return white, white, white, alpha


write_tga('sv.tga', 128, 128, sv)
write_tga('hue.tga', 256, 2, lambda hue, y: (*colorsys.hsv_to_rgb(hue, 1, 1), 1))
print('Generated SV and hue picker textures:', ROOT)
