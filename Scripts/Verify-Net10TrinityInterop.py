#!/usr/bin/env python3
"""Independent SV Trinity -> glTF / skeleton / animation / material interoperability test.

This validates gftool output in temporary CI files, NOT native TRMDL editing in Switch-Toolbox.
No game assets are committed, uploaded, or published.
"""
import argparse
import base64
import math
import struct
import copy
import hashlib
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit


def validate_asset_refs(gltf: dict, basedir: Path):
    checked = {}
    for category in ("buffers", "images"):
        for item in gltf.get(category, []):
            uri = item.get("uri")
            if not uri or uri.startswith("data:"):
                continue
            if urlsplit(uri).scheme:
                raise ValueError("Remote asset reference unexpectedly present: " + uri)
            target = (basedir / unquote(uri)).resolve()
            if not target.is_relative_to(basedir.resolve()):
                raise ValueError("glTF path escapes export directory")
            if not target.is_file():
                raise FileNotFoundError(str(target))
            checked[uri] = hashlib.sha256(target.read_bytes()).hexdigest()
    return checked



# Follow glTF 2.0 accessor layout, including tightly packed and strided views.
# These tests operate on *exported* glTF, not direct Trinity binary rewriting.
COMPONENTS = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
TYPE_LENGTHS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}

def buffer_bytes(doc, parent, buffer_index):
    b = doc["buffers"][buffer_index]
    uri = b.get("uri", "")
    if uri.startswith("data:"):
        return base64.b64decode(uri.split(",", 1)[1])
    if not uri or urlsplit(uri).scheme:
        raise ValueError("Unsupported glTF binary buffer URI: " + uri)
    path = (parent / unquote(uri)).resolve()
    if not path.is_relative_to(parent.resolve()):
        raise ValueError("Buffer escapes export directory")
    return path.read_bytes()

def accessor_layout(doc, accessor_index):
    acc = doc["accessors"][accessor_index]
    if "sparse" in acc:
        raise NotImplementedError("Sparse accessors need a separate validation path")
    view = doc["bufferViews"][acc["bufferView"]]
    fmt = COMPONENTS[acc["componentType"]]
    n = TYPE_LENGTHS[acc["type"]]
    width = struct.calcsize("<" + fmt * n)
    stride = view.get("byteStride", width)
    if stride < width:
        raise ValueError("Invalid glTF accessor stride")
    start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    return acc, view, fmt, n, width, stride, start

def values_from_accessor(doc, parent, index, limit=None):
    acc, view, fmt, n, width, stride, start = accessor_layout(doc, index)
    raw = buffer_bytes(doc, parent, view["buffer"])
    count = min(acc["count"], limit) if limit else acc["count"]
    values = []
    for i in range(count):
        off = start + i * stride
        if off + width > len(raw):
            raise ValueError("Truncated glTF binary accessor")
        vals = struct.unpack_from("<" + fmt * n, raw, off)
        if acc.get("normalized") and acc["componentType"] != 5126:
            typ = acc["componentType"]
            maxval = {5120:127,5121:255,5122:32767,5123:65535}.get(typ)
            if maxval:
                vals = tuple(max(-1.0, val / maxval) if typ in (5120,5122)
                             else val / maxval for val in vals)
        values.append(vals)
    return values

def verify_vertex_buffer_edit(doc, file):
    """Modify an actual exported vertex position in a copied .bin, reload via glTF accessors."""
    target = None
    for mesh in doc.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            idx = primitive.get("attributes", {}).get("POSITION")
            if idx is None:
                continue
            acc, view, fmt, n, width, stride, start = accessor_layout(doc, idx)
            if fmt == "f" and n == 3 and acc["count"] >= 1:
                target = (idx, view, start)
                break
        if target:
            break
    if target is None:
        raise AssertionError("No editable float VEC3 POSITION accessor in real mesh")
    idx, view, off = target
    old_pos = values_from_accessor(doc, file.parent, idx, limit=1)[0]
    buffer_index = view["buffer"]
    raw = bytearray(buffer_bytes(doc, file.parent, buffer_index))
    old_x = struct.unpack_from("<f", raw, off)[0]
    new_x = old_x + 0.125
    if not math.isfinite(new_x):
        raise AssertionError("Non-finite real mesh position")
    struct.pack_into("<f", raw, off, new_x)
    modified = file.with_name(file.stem + ".vertex-validated.bin")
    modified.write_bytes(raw)
    edited = copy.deepcopy(doc)
    edited["buffers"][buffer_index]["uri"] = modified.name
    output = file.with_name(file.stem + ".vertex-validated.gltf")
    output.write_text(json.dumps(edited, ensure_ascii=False), encoding="utf-8")
    reread = json.loads(output.read_text(encoding="utf-8"))
    updated_pos = values_from_accessor(reread, output.parent, idx, limit=1)[0]
    assert abs(updated_pos[0] - new_x) <= 1e-6, "Vertex edit missing after save/reload"
    assert updated_pos[1:] == old_pos[1:], "Unedited vertex components changed"
    assert reread["meshes"] == doc["meshes"], "Mesh descriptors changed"
    assert buffer_bytes(doc, file.parent, buffer_index)[off+4:] == raw[off+4:], "Unexpected later buffer bytes changed"
    assert buffer_bytes(doc, file.parent, buffer_index)[:off] == raw[:off], "Unexpected earlier buffer bytes changed"
    validate_asset_refs(reread, output.parent)
    print("DEEP_GLTF_VERTEX_EDIT PASSED originalX=%s newX=%s buffer=%s" %
          (old_x, updated_pos[0], modified.name))
    return {"vertex_edit_readback": True, "position_accessor": idx,
            "original_x": old_x, "edited_x": updated_pos[0]}

def matmul(a, b):
    return [sum(a[k * 4 + row] * b[col * 4 + k] for k in range(4))
            for col in range(4) for row in range(4)]

def node_matrix(node, pose):
    if "matrix" in node and not pose:
        return list(node["matrix"])
    t = pose.get("translation", node.get("translation", [0,0,0]))
    q = pose.get("rotation", node.get("rotation", [0,0,0,1]))
    s = pose.get("scale", node.get("scale", [1,1,1]))
    x,y,z,w = q
    return [
        (1-2*y*y-2*z*z)*s[0], (2*x*y+2*z*w)*s[0], (2*x*z-2*y*w)*s[0], 0,
        (2*x*y-2*z*w)*s[1], (1-2*x*x-2*z*z)*s[1], (2*y*z+2*x*w)*s[1], 0,
        (2*x*z+2*y*w)*s[2], (2*y*z-2*x*w)*s[2], (1-2*x*x-2*y*y)*s[2], 0,
        t[0], t[1], t[2], 1
    ]

def normalized_quat(q):
    length = math.sqrt(sum(v*v for v in q))
    return [v/length for v in q] if length > 0 else [0,0,0,1]

def lerp(a,b,u,path):
    if path == "rotation":
        # Normalize and use shortest-path interpolation for small animation steps.
        bb = list(b)
        if sum(x*y for x,y in zip(a,b)) < 0:
            bb = [-v for v in bb]
        return normalized_quat([x+(y-x)*u for x,y in zip(a,bb)])
    return [x+(y-x)*u for x,y in zip(a,b)]

def sampled_channel(doc, parent, anim, channel, t):
    samp = anim["samplers"][channel["sampler"]]
    times = [v[0] for v in values_from_accessor(doc, parent, samp["input"])]
    data = values_from_accessor(doc, parent, samp["output"])
    interp = samp.get("interpolation", "LINEAR")
    if not times:
        raise AssertionError("Empty animation time accessor")
    if interp == "CUBICSPLINE":
        assert len(data) == 3*len(times), "Invalid cubic animation data"
        values = [data[3*i+1] for i in range(len(times))]
    else:
        assert len(data) == len(times), "Mismatched animation frame count"
        values = data
    i = 0
    while i+1 < len(times) and t > times[i+1]:
        i += 1
    if i+1 == len(times) or t <= times[0]:
        return list(values[i if t > times[0] else 0])
    duration = times[i+1]-times[i]
    frac = (t-times[i])/duration if duration else 0
    if interp == "STEP":
        return list(values[i])
    if interp == "CUBICSPLINE":
        m0 = data[3*i+2]
        m1 = data[3*(i+1)]
        h00 = 2*frac**3-3*frac**2+1
        h10 = frac**3-2*frac**2+frac
        h01 = -2*frac**3+3*frac**2
        h11 = frac**3-frac**2
        out = [h00*a+h10*duration*b+h01*c+h11*duration*d
               for a,b,c,d in zip(values[i],m0,values[i+1],m1)]
        return normalized_quat(out) if channel["target"]["path"] == "rotation" else out
    return lerp(values[i], values[i+1], frac, channel["target"]["path"])

def verify_animated_skinning(doc, file):
    """Sample real exported bone keyframes and calculate animated joint world matrices."""
    skins, nodes, animations = doc.get("skins",[]), doc.get("nodes",[]), doc.get("animations",[])
    if not animations or not skins:
        raise AssertionError("Real model has no skeleton/animation")
    parents = {}
    for i,node in enumerate(nodes):
        for child in node.get("children",[]):
            parents[child] = i
    joint_ids = set(j for skin in skins for j in skin.get("joints", []))
    anim = animations[0]
    relevant = [c for c in anim["channels"]
                if c.get("target",{}).get("node") in joint_ids
                and c.get("target",{}).get("path") in ("rotation","translation","scale")]
    if not relevant:
        raise AssertionError("Animation does not target any skin joint")
    all_times = []
    for c in relevant:
        s = anim["samplers"][c["sampler"]]
        times = [v[0] for v in values_from_accessor(doc,file.parent,s["input"])]
        all_times.extend(times)
    all_times = sorted(set(all_times))
    if len(all_times) < 2 or all_times[0] == all_times[-1]:
        raise AssertionError("No animation duration to evaluate")
    samples = (all_times[0], 0.5*(all_times[0]+all_times[-1]), all_times[-1])
    matrices = []
    for moment in samples:
        patches = {}
        for c in relevant:
            target = c["target"]
            patches.setdefault(target["node"],{})[target["path"]] = sampled_channel(
                doc,file.parent,anim,c,moment)
        cache={}
        def world(index):
            if index in cache:
                return cache[index]
            local = node_matrix(nodes[index], patches.get(index,{}))
            cache[index] = matmul(world(parents[index]),local) if index in parents else local
            return cache[index]
        matrices.append({joint:world(joint) for joint in joint_ids})
    deltas = [max(abs(x-y) for x,y in zip(matrices[0][j],matrices[1][j]))
              for j in joint_ids]
    moved = sum(d>1e-7 for d in deltas)
    if not moved:
        deltas = [max(abs(x-y) for x,y in zip(matrices[0][j],matrices[2][j]))
                  for j in joint_ids]
        moved = sum(d>1e-7 for d in deltas)
    if not moved:
        raise AssertionError("Bone animation transforms do not change over sampled frames")
    if not all(math.isfinite(v) for frame in matrices for matrix in frame.values() for v in matrix):
        raise AssertionError("Non-finite bone transform")
    # Confirm the exported JOINTS_0 / WEIGHTS_0 are physically usable:
    # CPU-skin actual mesh vertices at multiple animation timestamps.
    # This does not claim that OpenGL rendered the skinned result.
    weighted_vertices = 0
    deformed_vertices = 0
    for node in nodes:
        if "mesh" not in node or "skin" not in node:
            continue
        skin = skins[node["skin"]]
        joint_mapping = skin["joints"]
        inv_bind = (values_from_accessor(doc, file.parent, skin["inverseBindMatrices"])
                    if "inverseBindMatrices" in skin else
                    [tuple([1 if i%5 == 0 else 0 for i in range(16)])
                     for _ in joint_mapping])
        if len(inv_bind) != len(joint_mapping):
            raise AssertionError("Inverse-bind matrix count does not match joints")
        for primitive in doc["meshes"][node["mesh"]].get("primitives",[]):
            attrs = primitive.get("attributes",{})
            if not all(k in attrs for k in ("POSITION","JOINTS_0","WEIGHTS_0")):
                continue
            count = min(doc["accessors"][attrs["POSITION"]]["count"], 128)
            positions = values_from_accessor(doc,file.parent,attrs["POSITION"],count)
            joints = values_from_accessor(doc,file.parent,attrs["JOINTS_0"],count)
            weights = values_from_accessor(doc,file.parent,attrs["WEIGHTS_0"],count)
            if not (len(positions) == len(joints) == len(weights)):
                raise AssertionError("Geometry, joints and weights have inconsistent vertex counts")
            for position,ids,vals in zip(positions,joints,weights):
                if len(ids) != len(vals):
                    raise AssertionError("Joint/weight vector size mismatch")
                valid = [(int(j),float(w)) for j,w in zip(ids,vals) if w > 1e-6]
                if not valid:
                    continue
                total_weight = sum(w for _,w in valid)
                if not (0.95 <= total_weight <= 1.05):
                    raise AssertionError("Invalid normalized skin weight sum")
                if any(j < 0 or j >= len(joint_mapping) for j,_ in valid):
                    raise AssertionError("Skin vertex references a nonexistent joint")
                weighted_vertices += 1
                posed=[]
                for frame in matrices:
                    xyz=[0.,0.,0.]
                    for j,w in valid:
                        bone=matmul(frame[joint_mapping[j]],inv_bind[j])
                        x,y,z=position[:3]
                        xyz[0]+=w*(bone[0]*x+bone[4]*y+bone[8]*z+bone[12])
                        xyz[1]+=w*(bone[1]*x+bone[5]*y+bone[9]*z+bone[13])
                        xyz[2]+=w*(bone[2]*x+bone[6]*y+bone[10]*z+bone[14])
                    posed.append(xyz)
                if max(abs(a-b) for a,b in zip(posed[0],posed[1])) > 1e-6:
                    deformed_vertices += 1
    if weighted_vertices == 0:
        raise AssertionError("No real skinned mesh vertices sampled")
    if deformed_vertices == 0:
        raise AssertionError("Animated joints did not deform sampled weighted vertices")
    print("DEEP_GLTF_CPU_SKINNING PASSED weighted=%s deformed=%s" %
          (weighted_vertices,deformed_vertices))
    print("DEEP_GLTF_SKELETON PASSED joints=%s channels=%s moving=%s sampleTimes=%s" %
          (len(joint_ids),len(relevant),moved,samples))
    return {"sample_times":samples,"animated_bone_count":moved,
            "sampled_skin_joints":len(joint_ids),"animation_channels":len(relevant),
            "cpu_skinned_vertices":weighted_vertices,"deformed_vertices":deformed_vertices}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gltf", type=Path, required=True)
    ap.add_argument("--require-animation", action="store_true")
    ap.add_argument("--min-lods", type=int, default=0)
    args = ap.parse_args()
    file = args.gltf.resolve()
    source = json.loads(file.read_text(encoding="utf-8-sig"))
    assert source.get("asset", {}).get("version", "").startswith("2."), "Not glTF 2"
    nodes, meshes, skins, materials = (source.get(k, []) for k in ("nodes", "meshes", "skins", "materials"))
    animations = source.get("animations", [])
    assert len(nodes) and len(meshes) and len(skins), "No geometry, nodes or skins"
    assert len(materials), "No materials exported"
    assert sum(len(m.get("primitives", [])) for m in meshes) > 0, "Empty mesh primitives"
    assert all(len(s.get("joints", [])) > 0 for s in skins), "Empty skeleton joints"
    if args.require_animation:
        assert animations and all(anim.get("channels") for anim in animations), "No animation channels"
    if args.min_lods:
        assert len(meshes) >= args.min_lods, "Too few LOD meshes"
    before = validate_asset_refs(source, file.parent)
    geometry_result = verify_vertex_buffer_edit(source, file)
    animation_result = verify_animated_skinning(source, file) if args.require_animation else {}

    edited = copy.deepcopy(source)
    material = edited["materials"][0]
    pbr = material.setdefault("pbrMetallicRoughness", {})
    old_color = pbr.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
    pbr["baseColorFactor"] = [0.375, 0.625, 0.875, float(old_color[3])]
    dest = file.with_name(file.stem + ".material-validated.gltf")
    dest.write_text(json.dumps(edited, ensure_ascii=False, indent=2), encoding="utf-8")
    reloaded = json.loads(dest.read_text(encoding="utf-8"))
    assert reloaded["materials"][0]["pbrMetallicRoughness"]["baseColorFactor"] == pbr["baseColorFactor"]
    assert reloaded["meshes"] == source["meshes"], "Geometry mutated by material metadata edit"
    assert reloaded.get("skins") == source.get("skins"), "Skeleton mutated"
    assert reloaded.get("animations") == source.get("animations"), "Animations mutated"
    assert validate_asset_refs(reloaded, file.parent) == before, "Texture / buffer assets mutated"
    print(json.dumps({
        "result": "PASS", "scope": "external SV gftool Trinity-to-glTF only",
        "model": file.stem, "meshes": len(meshes), "skins": len(skins),
        "bones": sum(len(s.get("joints", [])) for s in skins),
        "materials": len(materials), "animations": len(animations),
        "assets_checked": len(before),
        "material_edit_reloaded": True, "geometry_unchanged": True,
        **geometry_result, **animation_result,
        "native_Trinity_import_roundtrip": False,
        "Switch_Toolbox_native_Trinity_edit": False
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
