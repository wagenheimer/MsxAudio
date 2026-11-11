"""Generates the MSXScope application icon (pixel art, no external assets).

The icon is a rounded dark screen with an amber frame and a cyan-to-green waveform, matching the
visualizer palette. It is drawn natively at 16 and 32 px; the other ICO sizes are integer scalings,
so the pixels stay crisp.

Run from anywhere:

    python tools/make_icon.py

Outputs:
    tools/MsxAudioVisualizer/icon.ico   (16, 32, 48, 64, 128, 256)
    tools/MsxAudioVisualizer/icon.png   (256, used for the window icon)
    docs/media/icon.png                 (256, used by the README)
"""

import io
import math
import struct
from pathlib import Path

from PIL import Image

BACKGROUND = (15, 20, 29, 255)
AMBER = (255, 196, 64, 255)
GRID = (38, 50, 66, 255)
CYAN = (79, 195, 247, 255)
GREEN = (105, 219, 124, 255)
CLEAR = (0, 0, 0, 0)


def render(size: int) -> Image.Image:
    """Draws the icon at its native size (16 or 32)."""
    image = Image.new("RGBA", (size, size), CLEAR)
    pixels = image.load()
    radius = max(2, size // 7)

    # Rounded background.
    for y in range(size):
        for x in range(size):
            cx = min(x, size - 1 - x)
            cy = min(y, size - 1 - y)
            inside = True
            if cx < radius and cy < radius:
                dx = radius - cx - 0.5
                dy = radius - cy - 0.5
                inside = dx * dx + dy * dy <= radius * radius
            if inside:
                pixels[x, y] = BACKGROUND

    # Amber frame following the rounded mask.
    for y in range(size):
        for x in range(size):
            if pixels[x, y] == CLEAR:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if nx < 0 or ny < 0 or nx >= size or ny >= size or pixels[nx, ny] == CLEAR:
                    pixels[x, y] = AMBER
                    break

    # Centre line.
    middle = size // 2
    for x in range(size // 6, size - size // 6):
        if pixels[x, middle] == BACKGROUND:
            pixels[x, middle] = GRID

    # Waveform (connected per pixel column, cyan then green).
    amplitude = size * (0.26 if size <= 16 else 0.28)
    thickness = 1 if size <= 16 else 2
    left = size * 0.16
    right = size * 0.84
    span = max(1, right - left)

    previous = None
    for i in range(int(span) + 1):
        x = int(left + i)
        t = min(1.0, i / span)
        y = int(round(middle - math.sin(t * math.pi * 2) * amplitude))
        color = CYAN if t < 0.55 else GREEN

        low, high = (y, y) if previous is None else (min(previous, y), max(previous, y))
        for row in range(low, high + 1):
            for k in range(thickness):
                py = row + k
                if 0 <= x < size and 0 <= py < size and pixels[x, py] != CLEAR:
                    pixels[x, py] = color

        previous = y

    return image


def png_bytes(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def write_ico(path: Path, frames: list[tuple[int, Image.Image]]) -> None:
    """Writes an ICO with PNG-compressed frames (supported by Windows Vista and later)."""
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)

    entries = b""
    payload = b""

    for size, image in frames:
        data = png_bytes(image)
        dimension = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        payload += data

    path.write_bytes(header + entries + payload)


def main() -> None:
    root = Path(__file__).resolve().parent
    tool = root / "MsxAudioVisualizer"
    media = root.parent / "docs" / "media"
    media.mkdir(parents=True, exist_ok=True)

    small = render(16)
    large = render(32)

    frames = [
        (16, small),
        (32, large),
        (48, small.resize((48, 48), Image.NEAREST)),
        (64, large.resize((64, 64), Image.NEAREST)),
        (128, large.resize((128, 128), Image.NEAREST)),
        (256, large.resize((256, 256), Image.NEAREST)),
    ]

    write_ico(tool / "icon.ico", frames)

    large.resize((256, 256), Image.NEAREST).save(tool / "icon.png")
    large.resize((256, 256), Image.NEAREST).save(media / "icon.png")

    print(f"icon.ico  : {tool / 'icon.ico'}")
    print(f"icon.png  : {tool / 'icon.png'}")
    print(f"readme    : {media / 'icon.png'}")


if __name__ == "__main__":
    main()
