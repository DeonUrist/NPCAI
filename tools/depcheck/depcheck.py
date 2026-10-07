# Apocaplayer is optional for NPCAI: every method that touches its types (ModAPI, ModAPI.Character) must be in ApBody (run only once
# ApBody.Available said Apocaplayer 2.2.0+ is there), no field may be typed with them. Reads NPCAI.dll's metadata (pip install dnfile).
# Usage: python3 depcheck.py NPCAI.dll   (exit 1 when something outside ApBody touches Apocaplayer)
import sys, struct, dnfile
pe = dnfile.dnPE(sys.argv[1]); md = pe.net.mdtables
S = lambda x: str(x) if x is not None else ""
aref = {i + 1: S(r.Name) for i, r in enumerate(md.AssemblyRef)}
api_tr = {i + 1 for i, t in enumerate(md.TypeRef) if t.ResolutionScope is not None and t.ResolutionScope.table is not None and t.ResolutionScope.table.name == "AssemblyRef" and aref.get(t.ResolutionScope.row_index) == "Apocaplayer"}
for i, t in enumerate(md.TypeRef):
    sc = t.ResolutionScope
    if sc is not None and sc.table is not None and sc.table.name == "TypeRef" and sc.row_index in api_tr: api_tr.add(i + 1)
api_mr = {i + 1 for i, m in enumerate(md.MemberRef) if m.Class is not None and m.Class.table is not None and m.Class.table.name == "TypeRef" and m.Class.row_index in api_tr}
print("Apocaplayer types used:", sorted(S(md.TypeRef[i - 1].TypeName) for i in api_tr), "-", len(api_mr), "members")
def owner(mi):
    for t in md.TypeDef:
        ml = t.MethodList
        if ml and len(ml) > 0 and ml[0].row_index <= mi <= ml[-1].row_index: return S(t.TypeNamespace) + "." + S(t.TypeName)
    return "?"
ok, bad = [], []
data = pe.__data__
for mi, m in enumerate(md.MethodDef):
    if not m.Rva: continue
    off = pe.get_offset_from_rva(m.Rva); h = data[off]
    if h & 3 == 2: code = data[off + 1: off + 1 + (h >> 2)]
    else:
        flags, _, size, _ = struct.unpack_from("<HHII", data, off); code = data[off + (flags >> 12) * 4: off + (flags >> 12) * 4 + size]
    hit = any((struct.unpack_from("<I", code, k)[0] >> 24 == 0x0A and struct.unpack_from("<I", code, k)[0] & 0xFFFFFF in api_mr) or
              (struct.unpack_from("<I", code, k)[0] >> 24 == 0x01 and struct.unpack_from("<I", code, k)[0] & 0xFFFFFF in api_tr) for k in range(len(code) - 3))
    if hit:
        name = owner(mi + 1) + "::" + S(m.Name)
        (ok if name.startswith("NPCAI.ApBody::") and S(m.Name) not in ("Init", "get_Available", "WeaponKey", "IsPistol") else bad).append(name)
for f in md.Field:
    raw = bytes(f.Signature.value if hasattr(f.Signature, "value") else f.Signature or b"")
    if len(raw) >= 3 and raw[1] in (0x12, 0x11) and raw[2] & 3 == 1 and (raw[2] >> 2) in api_tr: bad.append("field " + S(f.Name))
print("inside ApBody:", ", ".join(sorted(x.split("::")[1] for x in ok)))
print("OK - nothing else touches Apocaplayer" if not bad else "OUTSIDE ApBody: " + ", ".join(sorted(bad)))
sys.exit(1 if bad else 0)
