import ctypes, subprocess, time, sys, io, os
from ctypes import wintypes
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

user32 = ctypes.WinDLL('user32', use_last_error=True)
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)
ntdll = ctypes.WinDLL('ntdll', use_last_error=True)
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception: user32.SetProcessDPIAware()
K32EnumProcesses = k32.K32EnumProcesses
QPIN = k32.QueryFullProcessImageNameW
EnumWindows = user32.EnumWindows
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, ctypes.c_void_p)

def pname(pid):
    try:
        h = k32.OpenProcess(0x1000, False, pid)
        if not h: return '?'
        b = ctypes.create_unicode_buffer(1024); n = wintypes.DWORD(1024)
        ok = QPIN(h, 0, b, ctypes.byref(n)); k32.CloseHandle(h)
        return b.value.rsplit('\\', 1)[-1] if ok else '?'
    except Exception: return '?'

def pids_of(name):
    arr = (wintypes.DWORD * 8192)(); need = wintypes.DWORD()
    if not K32EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(need)): return []
    return [arr[i] for i in range(need.value // 4) if arr[i] and pname(arr[i]).lower() == name.lower()]

def winfo(h):
    cls = ctypes.create_unicode_buffer(256); user32.GetClassNameW(h, cls, 256)
    ttl = ctypes.create_unicode_buffer(512); user32.GetWindowTextW(h, ttl, 512)
    r = wintypes.RECT(); user32.GetWindowRect(h, ctypes.byref(r))
    pid = wintypes.DWORD(); user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    return {'h': h, 'pid': pid.value, 'cls': cls.value, 'title': ttl.value,
            'rect': (r.left, r.top, r.right - r.left, r.bottom - r.top), 'vis': bool(user32.IsWindowVisible(h))}

def all_wins(pid=None):
    out = {}
    def cb(h, lp):
        try:
            if not user32.IsWindow(h): return True
            i = winfo(h)
            if pid is None or i['pid'] == pid: out[h] = i
        except Exception: pass
        return True
    EnumWindows(WNDENUMPROC(cb), 0)
    return out

def kids(h):
    out = []
    def cb(c, lp):
        try:
            i = winfo(c); i['id'] = user32.GetDlgCtrlID(c); out.append(i)
        except Exception: pass
        return True
    user32.EnumChildWindows(h, WNDENUMPROC(cb), 0)
    return out

def kill_all(name):
    for pid in pids_of(name): subprocess.run(['taskkill', '/F', '/PID', str(pid)], capture_output=True)

def sample(rect, grid=6):
    sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    hdc = user32.GetDC(None); mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, sw, sh); gdi32.SelectObject(mem, bmp)
    ok = gdi32.BitBlt(mem, 0, 0, sw, sh, hdc, 0, 0, 0x00CC0020)
    pts = []
    if ok:
        x0, y0, w, h = rect
        for i in range(grid):
            for j in range(grid):
                px = max(0, min(sw-1, int(x0 + w*(i+0.5)/grid))); py = max(0, min(sh-1, int(y0 + h*(j+0.5)/grid)))
                c = gdi32.GetPixel(mem, px, py); pts.append((c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF))
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(None, hdc)
    return pts

def dpct(a, b):
    if not a or not b or len(a) != len(b): return -1.0
    return 100.0 * sum(1 for x, y in zip(a, b) if abs(x[0]-y[0]) > 8 or abs(x[1]-y[1]) > 8 or abs(x[2]-y[2]) > 8) / len(a)

def find_game_window(pid, wait=20):
    t0 = time.time(); best = None
    while time.time() - t0 < wait:
        time.sleep(0.4)
        for h, i in all_wins(pid).items():
            if i['cls'] == 'BASE' and i['vis'] and i['rect'][2] > 100:
                if best is None or (time.time() - t0) > 6:
                    return h, i
                best = h
    return (best, winfo(best)) if best else (None, None)

def freeze_test(hwnd, pid, label):
    info = winfo(hwnd)
    rect = info['rect']
    print('  [%s] 窗口 rect=%dx%d@%d,%d' % (label, rect[2], rect[3], rect[0], rect[1]))
    # 建立动态基线
    base_ok = False
    for _ in range(8):
        x1 = sample(rect); time.sleep(0.45); x2 = sample(rect)
        d = dpct(x1, x2)
        if d > 1.0:
            base_ok = True
            print('    基线：画面在运动，两次采样差异 %.1f%%' % d)
            break
    if not base_ok:
        print('    基线：画面静止(差异≈0)，该画面无法证明定格，跳过')
        return None
    a1 = sample(rect); time.sleep(0.9); a2 = sample(rect)
    print('    挂起前 差异 %.1f%%' % dpct(a1, a2))
    hp = k32.OpenProcess(0x0800, False, pid)
    st = ntdll.NtSuspendProcess(hp)
    print('    NtSuspendProcess -> 0x%08X' % st)
    time.sleep(1.4)
    b1 = sample(rect); time.sleep(1.4); b2 = sample(rect)
    print('    挂起中 差异 %.1f%%   <<< 0%% = 画面完全定格' % dpct(b1, b2))
    print('    挂起瞬间 vs 挂起中 = %.1f%% (小时=定格的就是挂起那一刻)' % dpct(a2, b1))
    time.sleep(3.0)   # 挂满 3 秒，模拟真实使用
    print('    (已挂起约 3 秒)')
    rt = ntdll.NtResumeProcess(hp)
    print('    NtResumeProcess -> 0x%08X' % rt)
    k32.CloseHandle(hp)
    # 恢复瞬间高频采样，检测是否出现"追帧爆发"
    burst = []
    prev = sample(rect)
    for _ in range(10):
        time.sleep(0.12)
        cur = sample(rect)
        burst.append(dpct(prev, cur)); prev = cur
    print('    恢复后 10 次高频采样差异: %s' % ' '.join('%.0f%%' % x for x in burst))
    print('    => 恢复后画面 %s' % ('持续变化(游戏正常继续)' if max(burst) > 1.0 else '静止(游戏停在暂停态或静止画面)'))
    return rect

print('=' * 74)
print('目标 A: th09 花映塚 (dgVoodoo2) —— 挂起定格与恢复行为')
print('=' * 74)
kill_all('th09.exe'); time.sleep(1.0)
D9 = r'E:\SteamLibrary\steamapps\common\th09'
p9 = subprocess.Popen([os.path.join(D9, 'th09.exe')], cwd=D9)
gh, info = find_game_window(p9.pid, 22)
print('  进程存活=%s 窗口=%s' % (p9.poll() is None, info))
if gh:
    r9 = freeze_test(gh, p9.pid, 'th09')
kill_all('th09.exe'); time.sleep(2.0)

print()
print('=' * 74)
print('目标 B: th06nc 红魔乡新典 (D3D11) —— 挂起定格与恢复行为')
print('=' * 74)
kill_all('th06nc.exe'); time.sleep(1.0)
subprocess.run(['cmd', '/c', 'start', '', 'steam://rungameid/4659620'], capture_output=True)
gh6 = None; pid6 = None
t0 = time.time()
while time.time() - t0 < 30:
    time.sleep(0.7)
    ps = pids_of('th06nc.exe')
    if ps:
        pid6 = ps[0]
        h, i = find_game_window(pid6, 4)
        if h and i and i['rect'][2] > 200:
            gh6 = h; print('  窗口: cls=%s rect=%dx%d@%d,%d title=%r' % (i['cls'], i['rect'][2], i['rect'][3], i['rect'][0], i['rect'][1], i['title'][:40]))
            break
print('  进程=%s 窗口=%s' % (pid6, bool(gh6)))
if gh6:
    time.sleep(3.0)
    freeze_test(gh6, pid6, 'th06nc')
kill_all('th06nc.exe'); time.sleep(2.0)
print()
print('残留: th09=%s th10=%s th06nc=%s' % (pids_of('th09.exe'), pids_of('th10.exe'), pids_of('th06nc.exe')))
print('done')
