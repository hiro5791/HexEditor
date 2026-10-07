"""HexEditor のアイコン・ロゴ画像を作る (src/HexEditor.App/Assets)。

使い方: python tools/icon/make_icons.py
青の角丸の四角に、白の等幅の "0x" を置く。小さいサイズでも判読できるよう、文字は太く大きくする。
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "HexEditor.App" / "Assets"
FONT = Path("C:/Windows/Fonts/consolab.ttf")
TOP = (59, 130, 246)
BOTTOM = (30, 64, 175)


def tile(size: int, radius_ratio: float = 0.22) -> Image.Image:
    """size x size の角丸の四角にグラデーションと "0x" を描く。"""
    scale = 4
    big = size * scale
    gradient = Image.new("RGBA", (big, big))
    draw = ImageDraw.Draw(gradient)
    for y in range(big):
        t = y / max(1, big - 1)
        color = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,)
        draw.line([(0, y), (big, y)], fill=color)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=round(big * radius_ratio), fill=255)
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    image.paste(gradient, (0, 0), mask)

    text = "0x"
    font = ImageFont.truetype(str(FONT), round(big * 0.56))
    text_draw = ImageDraw.Draw(image)
    left, top, right, bottom = text_draw.textbbox((0, 0), text, font=font)
    x = (big - (right - left)) / 2 - left
    y = (big - (bottom - top)) / 2 - top
    text_draw.text((x, y), text, font=font, fill=(255, 255, 255, 255))
    return image.resize((size, size), Image.LANCZOS)


def centered(width: int, height: int, logo: int) -> Image.Image:
    """透明な width x height の中央にロゴを置く (横長のタイル、スプラッシュ用)。"""
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.paste(tile(logo), ((width - logo) // 2, (height - logo) // 2))
    return canvas


def main() -> None:
    outputs = {
        "Square44x44Logo.scale-200.png": tile(88),
        "Square44x44Logo.targetsize-24_altform-unplated.png": tile(24),
        "Square44x44Logo.targetsize-48_altform-lightunplated.png": tile(48),
        "Square150x150Logo.scale-200.png": centered(300, 300, 200),
        "Wide310x150Logo.scale-200.png": centered(620, 300, 200),
        "StoreLogo.png": tile(50),
        "LockScreenLogo.scale-200.png": tile(48),
        "SplashScreen.scale-200.png": centered(1240, 600, 300),
    }
    for name, image in outputs.items():
        image.save(ASSETS / name)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    tile(256).save(ASSETS / "AppIcon.ico", sizes=[(s, s) for s in sizes])
    print("written", len(outputs) + 1, "files to", ASSETS)


if __name__ == "__main__":
    main()
