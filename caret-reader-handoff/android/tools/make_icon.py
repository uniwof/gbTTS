# 生成启动图标：黑色圆角方块 + 白色细光标竖线 + 三道声波细弧。只用黑白灰。
import os, sys
from PIL import Image, ImageDraw
out = sys.argv[1]
def draw(size):
    S = size * 8
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    m = int(S * 0.06)
    d.rounded_rectangle([m, m, S - m, S - m], radius=int(S * 0.22), fill=(17, 17, 17, 255))
    w = max(1, int(S * 0.035))
    cx, cy = int(S * 0.36), S // 2
    h = int(S * 0.20)
    d.line([cx, cy - h, cx, cy + h], fill=(255, 255, 255, 255), width=w)            # 光标竖线
    d.line([cx - int(S*0.06), cy - h, cx + int(S*0.06), cy - h], fill=(255,255,255,255), width=w)
    d.line([cx - int(S*0.06), cy + h, cx + int(S*0.06), cy + h], fill=(255,255,255,255), width=w)
    for i, r in enumerate((0.10, 0.17, 0.24)):                                         # 声波
        R = int(S * r); x0 = int(S * 0.44)
        g = (255, 255, 255, 255) if i < 2 else (170, 170, 170, 255)
        d.arc([x0 - R, cy - R, x0 + R, cy + R], start=-45, end=45, fill=g, width=w)
    return im.resize((size, size), Image.LANCZOS)
for name, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
    p = os.path.join(out, f"mipmap-{name}"); os.makedirs(p, exist_ok=True)
    draw(px).save(os.path.join(p, "ic_launcher.png"), optimize=True)
draw(512).save(os.path.join(out, "..", "..", "..", "..", "build", "icon_preview.png"))
