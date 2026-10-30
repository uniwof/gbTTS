#!/usr/bin/env python3
"""
光标朗读 v0.6 悬浮球“看书的小人”精灵图（第一版是小猫，备份在 cat_backup/）。
纯代码绘制（PIL，8 倍超采样后缩小 = 抗锯齿），黑白灰三色，和软件的日式极简黑白界面一致。
输出：
  sprites.png        —— 一行 N 帧，每帧 CELL×CELL（RGBA，透明背景），程序启动时按 DPI 缩放一次
  sprites.json       —— 帧名 → 序号
  preview_*.png      —— 审图用（白底 / 深色桌面底、使用尺寸 60px 与放大图）
帧：idle（看书）、blink（眨眼）、page1..3（翻页）、talk0..2（念书：闭嘴 / 半张 / 张嘴）、
     look（暂停：抬头看你）、sleep（加载中 / 没有音色：闭眼）
"""
from PIL import Image, ImageDraw, ImageFilter
import json, math, os, sys

CELL = 160          # 输出每帧像素（够 250% 缩放下 60px 的球）
SS = 8              # 超采样倍数
W = CELL * SS
INK = (24, 24, 24, 255)
PAPER = (252, 252, 250, 255)
G1 = (232, 232, 230, 255)
G2 = (196, 196, 194, 255)
G3 = (140, 140, 138, 255)

K = 1.0            # 猫的缩放（球底不缩放）
def U(v):           # 设计坐标（0..100）→ 超采样像素
    return v * W / 100.0
def C(v, c=52.0):   # 猫身上的坐标：以 (50,52) 为中心缩小，离球的边圈留出呼吸空间
    return c + (v - c) * K

def ell(d, cx, cy, rx, ry, fill=None, outline=None, width=0):
    cx, cy, rx, ry = C(cx, 50), C(cy), rx * K, ry * K
    d.ellipse([U(cx - rx), U(cy - ry), U(cx + rx), U(cy + ry)], fill=fill, outline=outline, width=int(U(width)) if width else 0)

def poly(d, pts, fill=None, outline=None, width=0):
    d.polygon([(U(C(x, 50)), U(C(y))) for x, y in pts], fill=fill, outline=outline, width=int(U(width)) if width else 0)

def line(d, pts, width, fill=INK):
    pts = [(C(x, 50), C(y)) for x, y in pts]
    d.line([(U(x), U(y)) for x, y in pts], fill=fill, width=max(1, int(U(width))), joint="curve")
    r = U(width) / 2
    for x, y in (pts[0], pts[-1]):   # 圆头
        d.ellipse([U(x) - r, U(y) - r, U(x) + r, U(y) + r], fill=fill)

def arc_pts(cx, cy, rx, ry, a0, a1, n=24):
    return [(cx + rx * math.cos(math.radians(a0 + (a1 - a0) * i / n)), cy + ry * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]

def frame(eyes="down", mouth=0, page=0, bob=0.0, zz=False):
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # ---- 球底：纸白圆 + 墨线圈（悬浮球本体） ----
    global K
    K = 1.0
    d.ellipse([U(3), U(3), U(97), U(97)], fill=PAPER, outline=INK, width=int(U(3.4)))
    K = 0.86
    # 球内下半部极淡网点感的地面（一整块浅灰，不做碎点）
    by = bob
    # ---- 猫：头（大圆团）、耳朵 ----
    hx, hy = 50, 47 + by
    # ---- v0.6 改为“小人”（用户要的是看书的小人）：齐耳短发 + 刘海，耳朵露在两侧 ----
    # 耳朵（脸两侧的小半圆，纸白 + 墨线）
    ell(d, hx - 23.5, hy + 3, 3.6, 4.6, fill=PAPER, outline=INK, width=2.2)
    ell(d, hx + 23.5, hy + 3, 3.6, 4.6, fill=PAPER, outline=INK, width=2.2)
    # 后脑勺头发（一整块墨色大形）
    ell(d, hx, hy - 3, 26.5, 23.5, fill=INK)
    # 脸
    ell(d, hx, hy + 2, 22.5, 19.5, fill=PAPER, outline=INK, width=2.6)
    # 刘海：盖住额头的墨色块，下沿是三段柔和的弧（大形，不碎）
    fr = [(hx - 23.5, hy - 2)]
    for a in range(0, 181, 6):
        fr.append((hx - 23.5 + 47 * a / 180, hy - 7.5 + 3.2 * abs(math.sin(math.radians(a * 3)))))
    fr += [(hx + 23.5, hy - 2), (hx + 20, hy - 18), (hx, hy - 25), (hx - 20, hy - 18)]
    poly(d, fr, fill=INK)
    # 头顶一撮呆毛（小人的辨识点，单笔粗线）
    line(d, arc_pts(hx + 4, hy - 27, 5, 6, 200, 330, 14), 2.4)
    # 眼睛
    ey = hy + 2
    if eyes == "down":          # 低头看书：眼珠在下方，上眼皮压低（专注）
        for sx in (-10, 10):
            ell(d, hx + sx, ey + 1.6, 3.1, 3.3, fill=INK)          # 眼珠偏下 = 在看下面的书
    elif eyes == "blink":
        for sx in (-10, 10):
            line(d, [(hx + sx - 4, ey + 0.5), (hx + sx + 4, ey + 0.5)], 2.4)
    elif eyes == "look":        # 抬头看人：两个实心圆眼 + 小高光
        for sx in (-10, 10):
            ell(d, hx + sx, ey - 1, 3.6, 4.2, fill=INK)
            ell(d, hx + sx + 1.2, ey - 2.4, 1.1, 1.1, fill=PAPER)
    elif eyes == "sleep":
        for sx in (-10, 10):
            line(d, arc_pts(hx + sx, ey - 0.5, 4.2, 3.0, 20, 160), 2.4)   # ︶ 下弧 = 睡着
    # 腮红（浅灰，不加颜色）
    ell(d, hx - 16, ey + 6, 3.6, 2.2, fill=G2)
    ell(d, hx + 16, ey + 6, 3.6, 2.2, fill=G2)
    # 嘴
    my = ey + 7.5
    if mouth == 0:
        line(d, arc_pts(hx, my - 1.6, 3.4, 2.4, 20, 160, 14), 2.0)   # 小人：一道微笑弧（不是猫嘴 ω）
    elif mouth == 1:
        ell(d, hx, my + 0.8, 3.4, 2.6, fill=INK)
    else:
        ell(d, hx, my + 1.6, 4.4, 4.6, fill=INK)
        ell(d, hx, my + 3.6, 2.6, 1.6, fill=G3)   # 舌头（灰）
    # ---- 书：挡住下半身，双手扶书 ----
    bx, byy = 50, 76 + by * 0.3
    # 书本外形（打开的书，两页，略呈 V 字）
    left = [(bx - 27, byy - 9), (bx - 1, byy - 6), (bx - 1, byy + 13), (bx - 27, byy + 10)]
    right = [(bx + 27, byy - 9), (bx + 1, byy - 6), (bx + 1, byy + 13), (bx + 27, byy + 10)]
    poly(d, [(bx - 29, byy - 7), (bx, byy - 3.5), (bx + 29, byy - 7), (bx + 29, byy + 13), (bx, byy + 16), (bx - 29, byy + 13)], fill=INK)  # 封面（墨色）
    poly(d, left, fill=PAPER, outline=INK, width=1.6)
    poly(d, right, fill=PAPER, outline=INK, width=1.6)
    # 书页上的字行（三条灰线，左右页各三条）
    for i in range(2):
        yy = byy + 0.5 + i * 5.5
        line(d, [(bx - 22, yy - 1.0), (bx - 6, yy + 1.0)], 1.8, fill=G3)
        if page == 0:
            line(d, [(bx + 6, yy + 1.0), (bx + 22, yy - 1.0)], 1.8, fill=G3)
    # 翻页：一张页从右往左翻（三格）
    if page:
        t = {1: 0.25, 2: 0.55, 3: 0.85}[page]
        tipx = bx + 27 - t * 54
        lift = math.sin(t * math.pi) * 12
        poly(d, [(bx + 1, byy - 6), (tipx, byy - 9 - lift), (tipx, byy + 10 - lift * 0.7), (bx + 1, byy + 13)], fill=PAPER, outline=INK, width=1.4)
        if t > 0.5:   # 翻过去的页落在左页上：右页露出新的一页字
            for i in range(2):
                yy = byy + 0.5 + i * 5.5
                line(d, [(bx + 6, yy + 1.0), (bx + 22, yy - 1.0)], 1.8, fill=G3)
    # 手（扶在书的两边上沿）
    for sx in (-24, 24):
        ell(d, bx + sx, byy - 7.5, 5.2, 4.0, fill=PAPER, outline=INK, width=1.8)
    if zz:   # 睡觉：右上方一个小 z（整块形状，不碎）
        line(d, [(71, 17), (79, 17), (71, 25), (79, 25)], 2.4)
    out = im.resize((CELL, CELL), Image.LANCZOS)
    return out

FRAMES = [
    ("idle", dict()),
    ("blink", dict(eyes="blink")),
    ("page1", dict(page=1)), ("page2", dict(page=2)), ("page3", dict(page=3)),
    ("talk0", dict(mouth=0, bob=0.0)), ("talk1", dict(mouth=1, bob=0.6)), ("talk2", dict(mouth=2, bob=1.2)),
    ("look", dict(eyes="look")),
    ("sleep", dict(eyes="sleep", zz=True)),
]

def main(outdir):
    os.makedirs(outdir, exist_ok=True)
    imgs = [frame(**kw) for _, kw in FRAMES]
    sheet = Image.new("RGBA", (CELL * len(imgs), CELL), (0, 0, 0, 0))
    for i, im in enumerate(imgs):
        sheet.paste(im, (i * CELL, 0))
    sheet.save(os.path.join(outdir, "sprites.png"), optimize=True)
    json.dump({"cell": CELL, "frames": {n: i for i, (n, _) in enumerate(FRAMES)}}, open(os.path.join(outdir, "sprites.json"), "w"), indent=1)
    # 预览：使用尺寸 60px（100%）、90px（150%），白底和深色桌面
    for bg, name in (((246, 246, 246, 255), "light"), ((40, 52, 70, 255), "dark")):
        pv = Image.new("RGBA", (len(imgs) * 100 + 20, 260), bg)
        for i, im in enumerate(imgs):
            pv.alpha_composite(im.resize((60, 60), Image.LANCZOS), (20 + i * 100, 20))
            pv.alpha_composite(im.resize((90, 90), Image.LANCZOS), (5 + i * 100, 100))
        pv.save(os.path.join(outdir, f"preview_{name}.png"))
    big = Image.new("RGBA", (CELL * 5, CELL * 2), (246, 246, 246, 255))
    for i, im in enumerate(imgs):
        big.alpha_composite(im, ((i % 5) * CELL, (i // 5) * CELL))
    big.save(os.path.join(outdir, "preview_big.png"))
    print(os.path.getsize(os.path.join(outdir, "sprites.png")), "bytes")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)))
