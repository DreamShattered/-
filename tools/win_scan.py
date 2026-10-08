import ctypes, sys, io
from ctypes import wintypes
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

user32 = ctypes.WinDLL('user32', use_last_error=True)
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

QueryFullProcessImageNameW = k32.QueryFullProcessImageNameW
QueryFullProcessImageNameW.restype = wintypes.BOOL
EnumWindows = user32.EnumWindows
EnumWindows.restype = wintypes.BOOL
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

def proc_name(pid):
    try:
        h = k32.OpenProcess(0x1000, False, pid)
        if not h:
            return '?'
        buf = ctypes.create_unicode_buffer(1024)
        n = wintypes.DWORD(1024)
        ok = QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n))
        k32.CloseHandle(h)
        return buf.value.rsplit('\\', 1)[-1] if ok else '?'
    except Exception:
        return '?'

rows = []
def cb(hwnd, lp):
    try:
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if not user32.IsWindowVisible(hwnd):
            return True
        cls = ctypes.create_unicode_buffer(256); user32.GetClassNameW(hwnd, cls, 256)
        ttl = ctypes.create_unicode_buffer(512); user32.GetWindowTextW(hwnd, ttl, 512)
        r = wintypes.RECT(); user32.GetWindowRect(hwnd, ctypes.byref(r))
        style = user32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
        ex = user32.GetWindowLongW(hwnd, -20) & 0xFFFFFFFF
        rows.append({'z': len(rows), 'hwnd': hwnd, 'pid': pid.value,
                     'proc': proc_name(pid.value), 'cls': cls.value, 'title': ttl.value,
                     'rect': (r.left, r.top, r.right - r.left, r.bottom - r.top),
                     'popup': bool(style & 0x80000000), 'topmost': bool(ex & 8),
                     'transparent': bool(ex & 0x20), 'layered': bool(ex & 0x80000),
                     'noactivate': bool(ex & 0x8000000)})
    except Exception as e:
        rows.append({'z': -1, 'hwnd': 0, 'pid': 0, 'proc': 'ERR', 'cls': str(e), 'title': '', 'rect': (0,0,0,0),
                     'popup': False, 'topmost': False, 'transparent': False, 'layered': False, 'noactivate': False})
    return True

EnumWindows(WNDENUMPROC(cb), 0)
lines = ['%-4s %-9s %-7s %-24s %-20s %-22s %s' % ('z','hwnd','pid','process','class','size@pos','flags')]
for w in rows:
    flags = []
    if w['popup']: flags.append('POPUP')
    if w['topmost']: flags.append('TOPMOST')
    if w['transparent']: flags.append('CLICKTHRU')
    if w['layered']: flags.append('LAYERED')
    if w['noactivate']: flags.append('NOACT')
    lines.append('%-4d %-9s %-7d %-24s %-20s %-22s %s' % (w['z'], w['hwnd'], w['pid'], w['proc'][:24], w['cls'][:20],
        '%dx%d@%d,%d' % (w['rect'][2], w['rect'][3], w['rect'][0], w['rect'][1]), ','.join(flags)))
lines.append('visible top-level windows: %d' % len(rows))
out = '\n'.join(lines)
print(out)
try:
    open(r'D:\DSH\FocusFreeze\tools\win_scan_out.txt', 'w', encoding='utf-8').write(out)
except Exception:
    pass
