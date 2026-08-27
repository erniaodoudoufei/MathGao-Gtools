from base64 import b64encode
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets" / "icon" / "Gtools-source.png"
OUT = ROOT / "assets" / "icon" / "png"
APP = ROOT / "src" / "Gtools.App" / "Assets"
SVG = ROOT / "assets" / "icon" / "Gtools.svg"
OUT.mkdir(parents=True, exist_ok=True)
APP.mkdir(parents=True, exist_ok=True)

def master():
    src = Image.open(SOURCE).convert("RGBA")
    side = min(src.size)
    left = (src.width - side) // 2
    top = (src.height - side) // 2
    src = src.crop((left, top, left + side, top + side))
    # Replace only the supplied artwork's white exterior with transparency.
    mask = Image.new("L", src.size, 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, side - 1, side - 1), radius=round(side * .205), fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(max(0.6, side / 1800)))
    src.putalpha(ImageChops.multiply(src.getchannel("A"), mask))
    return src

def render(src, size):
    image = src.resize((size, size), Image.Resampling.LANCZOS)
    if size <= 24:
        image = image.filter(ImageFilter.UnsharpMask(radius=.45, percent=125, threshold=2))
    return image

source = master()
sizes = [16,20,24,32,48,64,128,256,512,1024]
images = {}
for size in sizes:
    images[size] = render(source, size)
    images[size].save(OUT / f"gtools-{size}.png", optimize=True)

images[256].save(APP / "Gtools.ico", format="ICO", sizes=[(16,16),(20,20),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
images[256].save(APP / "Gtools.png", optimize=True)

# Standalone SVG mother embedding the exact selected artwork.
encoded = b64encode(SOURCE.read_bytes()).decode("ascii")
SVG.write_text(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1254 1254">'
    '<defs><clipPath id="rounded"><rect width="1254" height="1254" rx="257"/></clipPath></defs>'
    f'<image width="1254" height="1254" clip-path="url(#rounded)" href="data:image/png;base64,{encoded}"/>'
    '</svg>', encoding="utf-8")
print(f"Generated {len(sizes)} PNG files, SVG and {APP / 'Gtools.ico'}")
