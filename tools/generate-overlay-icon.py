"""
Builds the hardlink overlay ICO by cloning Windows' own shortcut-arrow
overlay and re-tinting the blue arrow red.

Why: junctions and .lnk files both paint the canonical blue shortcut
arrow in the icon's lower-left (Windows does that automatically for
reparse points). A hardlink deserves the same visual vocabulary —
"this entry has an alternate origin" — but must remain instantly
distinguishable. Same shape, different colour reads correctly at a
glance and stays faithful to Windows' overlay positioning, shadow,
and multi-DPI bitmaps.

Source: SHGetStockIconInfo(SIID_SHORTCUT) points at the live
imageres.dll resource, so we pick up whatever Microsoft redesigns
the overlay to without re-authoring pixels. On Windows 10 1903+ the
bitmaps actually live in imageres.dll.mun under SystemResources; we
probe that path too because LOAD_LIBRARY_AS_DATAFILE on the stub
imageres.dll does not follow the MUI redirect.

Run from anywhere (Windows only):  python tools/generate-overlay-icon.py
"""

import ctypes
import io
import struct
from ctypes import wintypes
from pathlib import Path
from PIL import Image
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "src" / "PowerLink.ShellExt" / "assets" / "hardlink-overlay.ico"

SIID_SHORTCUT = 29
SHGSI_ICONLOCATION = 0x0
LOAD_LIBRARY_AS_DATAFILE = 0x02
RT_ICON = 3
RT_GROUP_ICON = 14

# Any pixel whose blue channel exceeds red by this much is treated as
# "arrow" and recoloured; everything else (white box, shadow) is left
# alone. 35 was picked empirically — low enough to catch antialiased
# edges, high enough to skip the slightly cool-tinted box (RGB ~232,235,241).
ARROW_MASK_THRESHOLD = 35


class SHSTOCKICONINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("hIcon", wintypes.HICON),
        ("iSysImageIndex", wintypes.INT),
        ("iIcon", wintypes.INT),
        ("szPath", wintypes.WCHAR * 260),
    ]


def _locate_shortcut_overlay() -> tuple[Path, int]:
    """Ask the shell where its shortcut-arrow icon lives, then resolve
    the MUI .mun file that actually holds the bitmaps on modern Windows."""
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    shell32.SHGetStockIconInfo.argtypes = [
        wintypes.UINT, wintypes.UINT, ctypes.POINTER(SHSTOCKICONINFO)
    ]
    shell32.SHGetStockIconInfo.restype = ctypes.c_long

    info = SHSTOCKICONINFO()
    info.cbSize = ctypes.sizeof(SHSTOCKICONINFO)
    hr = shell32.SHGetStockIconInfo(SIID_SHORTCUT, SHGSI_ICONLOCATION,
                                    ctypes.byref(info))
    if hr != 0:
        raise OSError(f"SHGetStockIconInfo failed, HRESULT=0x{hr & 0xFFFFFFFF:08X}")

    # iIcon is the ExtractIcon-style index: negative = -ResourceID.
    if info.iIcon >= 0:
        raise RuntimeError(
            "Expected negative iIcon (resource id); got zero-based index "
            f"{info.iIcon}. Extraction logic assumes RT_GROUP_ICON lookup."
        )
    res_id = -info.iIcon

    src = Path(info.szPath)
    mun = src.parent.parent / "SystemResources" / f"{src.name}.mun"
    return (mun if mun.exists() else src), res_id


_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_kernel32.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
_kernel32.LoadLibraryExW.restype = wintypes.HMODULE
_kernel32.FreeLibrary.argtypes = [wintypes.HMODULE]
_kernel32.FindResourceW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p]
_kernel32.FindResourceW.restype = wintypes.HANDLE
_kernel32.LoadResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
_kernel32.LoadResource.restype = wintypes.HANDLE
_kernel32.LockResource.argtypes = [wintypes.HANDLE]
_kernel32.LockResource.restype = ctypes.c_void_p
_kernel32.SizeofResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
_kernel32.SizeofResource.restype = wintypes.DWORD


def _load_resource(hmod, rtype: int, name_int: int) -> bytes | None:
    h = _kernel32.FindResourceW(hmod, ctypes.c_void_p(name_int), ctypes.c_void_p(rtype))
    if not h:
        return None
    size = _kernel32.SizeofResource(hmod, h)
    ptr = _kernel32.LockResource(_kernel32.LoadResource(hmod, h))
    return bytes((ctypes.c_ubyte * size).from_address(ptr))


def _frame_to_image(raw: bytes, w: int, h: int, bpp: int) -> Image.Image:
    """Decode one RT_ICON blob. Modern icons ship as embedded PNG for
    the 256 frame and as classic DIB (no file header) for smaller sizes —
    wrap DIBs in a synthetic 1-frame ICO so Pillow parses them."""
    if raw[:8] == b"\x89PNG\r\n\x1a\n":
        return Image.open(io.BytesIO(raw)).convert("RGBA")

    buf = io.BytesIO()
    buf.write(struct.pack("<HHH", 0, 1, 1))                    # ICONDIR
    wb = 0 if w >= 256 else w
    hb = 0 if h >= 256 else h
    buf.write(struct.pack("<BBBBHHII", wb, hb, 0, 0, 1, bpp, len(raw), 22))
    buf.write(raw)
    buf.seek(0)
    return Image.open(buf).convert("RGBA")


def extract_frames(dll: Path, group_id: int) -> list[Image.Image]:
    hmod = _kernel32.LoadLibraryExW(str(dll), None, LOAD_LIBRARY_AS_DATAFILE)
    if not hmod:
        raise OSError(f"LoadLibraryExW({dll}) failed: err={ctypes.get_last_error()}")

    try:
        grp = _load_resource(hmod, RT_GROUP_ICON, group_id)
        if grp is None:
            raise RuntimeError(f"{dll.name}: no RT_GROUP_ICON #{group_id}")

        # GRPICONDIR: reserved(2) type(2) count(2)
        _, typ, count = struct.unpack("<HHH", grp[:6])
        if typ != 1:
            raise RuntimeError(f"unexpected resource type {typ} (expected 1=ICON)")

        frames: list[Image.Image] = []
        offset = 6
        for _ in range(count):
            # GRPICONDIRENTRY is 14 bytes (the on-disk ICONDIRENTRY's final
            # ImageOffset field is replaced by a 2-byte resource id here).
            w, h, _c, _r, _p, bpp, _sz, icon_id = struct.unpack(
                "<BBBBHHIH", grp[offset:offset + 14]
            )
            offset += 14
            raw = _load_resource(hmod, RT_ICON, icon_id)
            if raw is None:
                continue
            frames.append(_frame_to_image(raw, w or 256, h or 256, bpp))
        return frames
    finally:
        _kernel32.FreeLibrary(hmod)


def recolor_arrow(img: Image.Image) -> Image.Image:
    """Swap blue-channel dominance into red-channel dominance on the
    arrow pixels only. Mapping (R,G,B)->(B,R,R) preserves the original
    shading and antialiased edges while yielding a saturated red."""
    arr = np.array(img)
    r, b, a = arr[..., 0], arr[..., 2], arr[..., 3]
    mask = (b.astype(np.int16) - r.astype(np.int16) > ARROW_MASK_THRESHOLD) & (a > 0)

    out = arr.copy()
    out[mask, 0] = arr[mask, 2]
    out[mask, 1] = arr[mask, 0]
    out[mask, 2] = arr[mask, 0]
    return Image.fromarray(out, "RGBA")


def write_ico(frames: list[Image.Image], path: Path) -> None:
    """Pillow's ICO writer down-scales a single bitmap to all sizes; we
    need per-size bitmaps, so assemble the ICO manually: ICONDIR header
    + ICONDIRENTRY x N + PNG blobs."""
    blobs: list[tuple[int, bytes]] = []
    for frame in frames:
        buf = io.BytesIO()
        frame.save(buf, format="PNG", optimize=True)
        blobs.append((frame.width, buf.getvalue()))

    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(blobs)))
    data_offset = 6 + 16 * len(blobs)
    for size, png in blobs:
        # Width/height byte of 0 encodes 256.
        w_byte = 0 if size >= 256 else size
        out.write(struct.pack(
            "<BBBBHHII",
            w_byte, w_byte, 0, 0,
            1, 32,
            len(png), data_offset,
        ))
        data_offset += len(png)
    for _, png in blobs:
        out.write(png)

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(out.getvalue())


def build_overlay() -> None:
    dll, group_id = _locate_shortcut_overlay()
    print(f"Source: {dll}  (RT_GROUP_ICON #{group_id})")

    frames = extract_frames(dll, group_id)
    if not frames:
        raise RuntimeError("no frames extracted")

    # Sort by size so Explorer picks correctly; dedupe in case the
    # resource group ships duplicates at the same canvas size.
    frames.sort(key=lambda im: im.width)
    seen: set[int] = set()
    frames = [im for im in frames if not (im.width in seen or seen.add(im.width))]
    frames = [recolor_arrow(im) for im in frames]

    write_ico(frames, OUTPUT)
    sizes = ", ".join(str(f.width) for f in frames)
    print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes, "
          f"{len(frames)} sizes: {sizes})")


if __name__ == "__main__":
    build_overlay()
