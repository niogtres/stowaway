"""Derive runtime brand assets from the masters in assets/brand/.

Run from anywhere:  python -I tools/generate-brand-assets.py
Requires Pillow (with WebP support). Outputs are committed, so this only needs rerunning when a master changes.
"""

from pathlib import Path
import shutil

from PIL import Image, features

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "assets" / "brand"

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
FAVICON_SIZES = [16, 32, 48]
# Empty states are shown at up to ~320 CSS px wide; 2x for high-DPI screens.
ILLUSTRATION_SIZE = (640, 480)
ILLUSTRATIONS = ["empty-inventory", "empty-plans", "empty-snapshots", "engine-offline"]


def write_ico(source: Image.Image, target: Path, sizes: list[int]) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    source.save(target, format="ICO", sizes=[(s, s) for s in sizes])
    print(f"  {target.relative_to(ROOT)}  ({', '.join(map(str, sizes))})")


def write_bmp(name: str, target: Path, expected: tuple[int, int]) -> None:
    image = Image.open(BRAND / f"{name}.png").convert("RGB")
    if image.size != expected:
        raise SystemExit(f"{name}.png must be {expected[0]}x{expected[1]}, got {image.size[0]}x{image.size[1]}")
    target.parent.mkdir(parents=True, exist_ok=True)
    image.save(target, format="BMP")
    print(f"  {target.relative_to(ROOT)}")


def main() -> None:
    if not features.check("webp"):
        raise SystemExit("Pillow was built without WebP support")

    logo = Image.open(BRAND / "logo-mark-1024.png").convert("RGBA")

    print("Icons")
    write_ico(logo, ROOT / "src" / "Stowaway.Service" / "app.ico", ICO_SIZES)
    write_ico(logo, ROOT / "web" / "public" / "favicon.ico", FAVICON_SIZES)
    shutil.copyfile(BRAND / "logo-mark.svg", ROOT / "web" / "public" / "favicon.svg")
    shutil.copyfile(BRAND / "logo-mark.svg", ROOT / "web" / "src" / "assets" / "logo-mark.svg")
    print("  web/public/favicon.svg, web/src/assets/logo-mark.svg")

    print("Installer")
    write_bmp("installer-dialog", ROOT / "installer" / "assets" / "dialog.bmp", (493, 312))
    write_bmp("installer-banner", ROOT / "installer" / "assets" / "banner.bmp", (493, 58))

    print("Illustrations")
    out_dir = ROOT / "web" / "src" / "assets" / "illustrations"
    out_dir.mkdir(parents=True, exist_ok=True)
    for name in ILLUSTRATIONS:
        image = Image.open(BRAND / f"{name}.png").convert("RGBA").resize(ILLUSTRATION_SIZE, Image.Resampling.LANCZOS)
        target = out_dir / f"{name}.webp"
        image.save(target, format="WEBP", quality=86, method=6)
        print(f"  {target.relative_to(ROOT)}  ({target.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
