"""
Generates a Windows shell overlay icon from the main PowerLink icon.

Per Microsoft's Win32 icon design guide:
  "Overlays go in bottom-left corner of icon, and should fill 25 percent
   of icon area."  (i.e. 50%x50% bounding box, except 16x16 which gets
   10x10 — slightly bigger to stay readable.)

Multi-res ICO covers every size Explorer uses (Vista+ never scales
overlays UP — missing sizes look wrong on hi-DPI thumbnails).

The previous PowerShell version went through System.Drawing.Icon.ToBitmap()
which silently dropped alpha for the multi-res source ICO. Pillow handles
it correctly.

Run from anywhere:  python tools/generate-overlay-icon.py
"""

import io
import struct
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "src" / "PowerLink.App" / "Assets" / "Icon.ico"
OUTPUT = ROOT / "src" / "PowerLink.ShellExt" / "assets" / "hardlink-overlay.ico"

# Microsoft Win32 icon design spec: badge dimension per canvas size.
BADGE_SIZES = {
    16: 10,
    24: 12,
    32: 16,
    48: 24,
    64: 32,
    96: 48,
    128: 64,
    256: 128,
}


def best_source_frame(ico_path: Path, target: int) -> Image.Image:
    """Pick the frame >= target, trim its transparent margins, then scale
    to target. Trimming first means the badge fills the lower-left quadrant
    edge-to-edge instead of inheriting the source ICO's centring padding."""
    with Image.open(ico_path) as im:
        sizes = sorted(im.ico.sizes())
        chosen = next((s for s in sizes if s[0] >= target), sizes[-1])
        im.size = chosen
        frame = im.copy().convert("RGBA")

    bbox = frame.getbbox()  # tightest non-transparent rectangle
    if bbox is not None:
        frame = frame.crop(bbox)

    if frame.size != (target, target):
        frame = frame.resize((target, target), Image.LANCZOS)
    return frame


def write_ico(frames: list[Image.Image], path: Path) -> None:
    """Pillow's ICO save scales one image to all sizes — no way to give it
    custom per-size content. So we encode each frame to PNG and assemble the
    ICO file ourselves: ICONDIR header + ICONDIRENTRY x N + PNG blobs."""
    blobs: list[tuple[int, bytes]] = []
    for frame in frames:
        buf = io.BytesIO()
        frame.save(buf, format="PNG", optimize=True)
        blobs.append((frame.width, buf.getvalue()))

    out = io.BytesIO()
    # ICONDIR (6 bytes): reserved=0, type=1 (ICO), count
    out.write(struct.pack("<HHH", 0, 1, len(blobs)))

    data_offset = 6 + 16 * len(blobs)
    for size, png in blobs:
        # 0 in width/height byte means 256
        w_byte = 0 if size >= 256 else size
        h_byte = w_byte
        # ICONDIRENTRY (16 bytes)
        out.write(struct.pack(
            "<BBBBHHII",
            w_byte, h_byte, 0, 0,  # width, height, color count, reserved
            1, 32,                  # planes, bit count
            len(png), data_offset,  # bytes in res, image offset
        ))
        data_offset += len(png)

    for _, png in blobs:
        out.write(png)

    path.write_bytes(out.getvalue())


def build_overlay() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)

    frames: list[Image.Image] = []
    for canvas_size, badge_size in BADGE_SIZES.items():
        canvas = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
        badge = best_source_frame(SOURCE, badge_size)
        canvas.paste(badge, (0, canvas_size - badge_size), badge)
        frames.append(canvas)

    write_ico(frames, OUTPUT)

    bytes_written = OUTPUT.stat().st_size
    sizes = [f.width for f in frames]
    print(f"Wrote {OUTPUT} ({bytes_written} bytes, {len(frames)} sizes: "
          f"{', '.join(str(s) for s in sizes)})")


if __name__ == "__main__":
    build_overlay()
