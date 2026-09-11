# -*- coding: utf-8 -*-
"""
Read the #US (user string) heap out of a .NET assembly, in heap order.

Their DLL is 0.1.1 with the literals replaced, so our own 0.1.1 build is the
English counterpart. Aligning the two heaps by position pairs each English
string with its translation without anyone guessing.

Reads bytes. Nothing from the third-party assembly is loaded or executed.
"""
import struct, sys, io, json


def u16(b, o): return struct.unpack_from('<H', b, o)[0]
def u32(b, o): return struct.unpack_from('<I', b, o)[0]


def us_heap(path):
    b = open(path, 'rb').read()
    pe = u32(b, 0x3C)
    assert b[pe:pe + 4] == b'PE\0\0', 'not a PE'
    coff = pe + 4
    nsec = u16(b, coff + 2)
    optsz = u16(b, coff + 16)
    opt = coff + 20
    magic = u16(b, opt)
    # data directories start after the standard + windows-specific fields
    dd = opt + (96 if magic == 0x10B else 112)
    cli_rva = u32(b, dd + 14 * 8)
    if cli_rva == 0:
        raise SystemExit('no CLI header: not a managed assembly')

    sections = []
    so = opt + optsz
    for i in range(nsec):
        s = so + i * 40
        vaddr, vsize = u32(b, s + 12), u32(b, s + 8)
        praw, sraw = u32(b, s + 20), u32(b, s + 16)
        sections.append((vaddr, vsize, praw, sraw))

    def off(rva):
        for vaddr, vsize, praw, sraw in sections:
            if vaddr <= rva < vaddr + max(vsize, sraw):
                return praw + (rva - vaddr)
        raise KeyError(hex(rva))

    cli = off(cli_rva)
    md = off(u32(b, cli + 8))
    assert b[md:md + 4] == b'BSJB', 'bad metadata signature'
    vlen = u32(b, md + 12)
    p = md + 16 + vlen
    p += 2                                  # flags
    nstreams = u16(b, p); p += 2

    heap = None
    for _ in range(nstreams):
        soff, ssize = u32(b, p), u32(b, p + 4)
        p += 8
        end = b.index(b'\0', p)
        name = b[p:end].decode('ascii')
        p = end + 1
        p = (p + 3) & ~3                    # names are 4-byte aligned
        if name == '#US':
            heap = b[md + soff: md + soff + ssize]
    if heap is None:
        raise SystemExit('no #US heap')

    def compressed(blob, i):
        x = blob[i]
        if x & 0x80 == 0:
            return x, i + 1
        if x & 0x40 == 0:
            return ((x & 0x3F) << 8) | blob[i + 1], i + 2
        return (((x & 0x1F) << 24) | (blob[i + 1] << 16) |
                (blob[i + 2] << 8) | blob[i + 3]), i + 4

    out, i = [], 0
    while i < len(heap):
        start = i
        n, i = compressed(heap, i)
        if n == 0:
            out.append((start, ''))
            continue
        raw = heap[i:i + n - 1]             # last byte is the terminal flag
        i += n
        try:
            out.append((start, raw.decode('utf-16-le')))
        except Exception:
            out.append((start, None))
    return out


if __name__ == '__main__':
    a = us_heap(sys.argv[1])
    print("%s: %d strings" % (sys.argv[1].split('\\')[-1], len(a)))
    if len(sys.argv) > 2:
        out = io.open(sys.argv[2], 'w', encoding='utf-8')
        json.dump([s for _, s in a], out, ensure_ascii=False, indent=0)
        out.close()
        print("written -> " + sys.argv[2])
