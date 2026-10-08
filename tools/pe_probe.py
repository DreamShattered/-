import struct, sys, os, re
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

KEYS = ['d3d8','d3d9','d3d10','d3d11','d3d12','dxgi','ddraw','opengl32','vulkan','sdl2','sdl3',
        'dinput','xinput','dsound','winmm','msacm','xaudio','steam_api','libEGL','libGLES',
        'd3dcompiler','nvapi','amdxc','gameinput']
KEYWORDS = re.compile(r'(?i)(' + '|'.join(KEYS) + r')')

def rva2off(sections, rva):
    for va, vsz, raw, rsz in sections:
        if va <= rva < va + max(vsz, rsz):
            return raw + (rva - va)
    return None

def probe(path):
    out = []
    with open(path,'rb') as f:
        data = f.read()
    out.append('===== ' + path)
    out.append('  size: %d bytes' % len(data))
    if data[:2] != b'MZ':
        out.append('  not PE'); return out
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe+4] != b'PE\x00\x00':
        out.append('  not PE'); return out
    machine, nsec, stamp, _sym, opt_size, chars = struct.unpack_from('<HHIIIH', data, pe+4)
    magic = struct.unpack_from('<H', data, pe+24)[0]
    is64 = magic == 0x20b
    sub = struct.unpack_from('<H', data, pe+24+68)[0]
    dllchar = struct.unpack_from('<H', data, pe+24+70)[0]
    out.append('  machine=%s subsystem=%s sections=%d link=%d.%d timestamp=%s' % (
        {0x14c:'x86',0x8664:'x64',0x1c0:'ARM',0xaa64:'ARM64'}.get(machine,hex(machine)),
        {2:'GUI',3:'CUI',1:'NATIVE'}.get(sub,str(sub)), nsec,
        struct.unpack_from('<H', data, pe+26)[0], (stamp >> 0) & 0xffff if False else 0,
        __import__('datetime').datetime.utcfromtimestamp(stamp).strftime('%Y-%m-%d %H:%M') if 0 < stamp < 4102444800 else str(stamp)))
    out.append('  dllchar=0x%04X (DYNAMIC_BASE=%s NX=%s LARGEADDR=%s)' % (
        dllchar, bool(dllchar & 0x40), bool(dllchar & 0x100), bool(dllchar & 0x20)))
    dd = pe + 24 + (112 if is64 else 96)
    imp_rva = struct.unpack_from('<I', data, dd + 8)[0]
    sec_off = pe + 24 + opt_size
    sections = []
    for i in range(nsec):
        o = sec_off + i*40
        name = data[o:o+8].rstrip(b'\x00').decode('latin1')
        vsz, va, rsz, raw = struct.unpack_from('<IIII', data, o+8)
        sections.append((va, vsz, raw, rsz))
    dlls = []
    if imp_rva:
        off = rva2off(sections, imp_rva)
        if off:
            i = 0
            while True:
                o = off + i*20
                desc = struct.unpack_from('<IIIII', data, o)
                if all(v == 0 for v in desc): break
                no = rva2off(sections, desc[3])
                if no is None: break
                end = data.index(b'\x00', no)
                dlls.append(data[no:end].decode('latin1', 'replace'))
                i += 1
    out.append('  imports(%d): %s' % (len(dlls), ', '.join(dlls)))
    hits = sorted(set(d.lower() for d in dlls if KEYWORDS.search(d)))
    out.append('  graphics/input: %s' % (', '.join(hits) if hits else '(none)'))
    txt = data.decode('latin1')
    found = {}
    for m in re.finditer(r'[ -~]{5,120}', txt):
        s = m.group(0)
        for k in KEYWORDS.finditer(s):
            key = k.group(1).lower()
            if key not in found: found[key] = s.strip()[:110]
    if found:
        out.append('  string hints:')
        for k, v in sorted(found.items()):
            out.append('    [%s] %s' % (k, v))
    return out

for p in sys.argv[1:]:
    if os.path.isfile(p):
        for line in probe(p): print(line)
    else:
        print('===== ' + p + '\n  missing')
