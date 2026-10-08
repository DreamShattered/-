import zlib, struct, os, math

OUT = r'D:\DSH\FocusFreeze'
def png_bytes(w, h, rgba_rows):
    # color type 6 = RGBA
    raw = b''.join(b'\x00' + r for r in rgba_rows)
    def ch(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return (b'\x89PNG\r\n\x1a\n'
            + ch(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + ch(b'IDAT', zlib.compress(raw, 9)) + ch(b'IEND', b''))

def build_frame(size, ss=3):
    # 超采样绘制，得到平滑边缘
    W = size * ss
    buf = bytearray(W * W * 4)  # RGBA
    r = int(W * 0.22)          # 圆角半径
    def put(x, y, c):
        if 0 <= x < W and 0 <= y < W:
            o = (y * W + x) * 4
            buf[o] = c[0]; buf[o+1] = c[1]; buf[o+2] = c[2]; buf[o+3] = c[3]
    def inside_round_rect(x, y, x0, y0, x1, y1, rr):
        if x < x0 or x > x1 or y < y0 or y > y1: return False
        cx = min(max(x, x0 + rr), x1 - rr); cy = min(max(y, y0 + rr), y1 - rr)
        dx = x - cx; dy = y - cy
        return dx*dx + dy*dy <= rr*rr
    # 底色：深色圆角方块 + 垂直渐变 + 细描边
    for y in range(W):
        for x in range(W):
            if inside_round_rect(x, y, 0, 0, W-1, W-1, r):
                t = y / (W - 1)
                col = (int(18 + 10*(1-t)), int(22 + 12*(1-t)), int(30 + 18*(1-t)), 255)
                put(x, y, col)
    # 描边
    for y in range(W):
        for x in range(W):
            if not inside_round_rect(x, y, 0, 0, W-1, W-1, r): continue
            if not inside_round_rect(x, y, 3*ss, 3*ss, W-1-3*ss, W-1-3*ss, max(1, r - 3*ss)):
                put(x, y, (96, 165, 250, 255))
    # 暂停符号：两条竖条
    bar_w = int(W * 0.115); bar_h = int(W * 0.44)
    gap = int(W * 0.10)
    top = (W - bar_h) // 2
    br = int(bar_w * 0.45)
    left_x = W // 2 - gap // 2 - bar_w
    right_x = W // 2 + gap // 2
    for (bx) in (left_x, right_x):
        for y in range(top, top + bar_h):
            for x in range(bx, bx + bar_w):
                if inside_round_rect(x, y, bx, top, bx + bar_w - 1, top + bar_h - 1, br):
                    put(x, y, (240, 245, 255, 255))
    # 降采样
    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            rs = gs = bs = as_ = 0
            for dy in range(ss):
                for dx in range(ss):
                    o = ((y*ss+dy) * W + (x*ss+dx)) * 4
                    rs += buf[o]; gs += buf[o+1]; bs += buf[o+2]; as_ += buf[o+3]
            n = ss*ss
            row += bytes((rs//n, gs//n, bs//n, as_//n))
        rows.append(bytes(row))
    return rows

sizes = [16, 32, 48, 64, 128, 256]
images = []
for s in sizes:
    rows = build_frame(s, ss=3 if s <= 64 else 2)
    data = png_bytes(s, s, rows)
    images.append((s, data))
    if s == 256:
        open(os.path.join(OUT, 'tools', 'icon_preview_256.png'), 'wb').write(data)

# 组装 ICO
n = len(images)
hdr = struct.pack('<HHH', 0, 1, n)
entries = b''
offset = 6 + 16*n
blobs = b''
for s, data in images:
    entries += struct.pack('<BBBBHHII', 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)
ico = hdr + entries + blobs
ico_path = os.path.join(OUT, 'app.ico')
open(ico_path, 'wb').write(ico)
print('ICO 已生成: %s (%d bytes, %d 个尺寸: %s)' % (ico_path, len(ico), n, sizes))
print('预览 PNG: %s' % os.path.join(OUT, 'tools', 'icon_preview_256.png'))
