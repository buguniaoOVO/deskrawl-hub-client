from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parent
source = ROOT / "Assets" / "demon-icon.png"
out = ROOT / "Assets" / "Deskrawl.ico"

if not source.is_file():
    raise FileNotFoundError(source)

image = Image.open(source).convert("RGBA")
if image.width != image.height:
    edge = min(image.size)
    left = (image.width - edge) // 2
    top = (image.height - edge) // 2
    image = image.crop((left, top, left + edge, top + edge))

out.parent.mkdir(parents=True, exist_ok=True)
image.save(out, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(out)
