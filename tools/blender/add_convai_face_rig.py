"""Add a compact CC4-compatible facial target set to an existing humanoid mesh.

The Sketchfab interviewers contain a valid humanoid body rig but no facial
blendshapes. This authoring pass derives restrained mouth/eye/brow targets from
the Head-weighted surface so Convai's timestamped CC4 stream can drive them.
Original FBX files are never overwritten.
"""

from __future__ import annotations

import sys
from pathlib import Path

import bpy
from mathutils import Vector


def arguments() -> tuple[Path, Path]:
    args = sys.argv[sys.argv.index("--") + 1 :]
    if len(args) != 2:
        raise SystemExit("usage: blender --background --python add_convai_face_rig.py -- input.fbx output.fbx")
    return Path(args[0]).resolve(), Path(args[1]).resolve()


def weight(vertex, group_index: int) -> float:
    for membership in vertex.groups:
        if membership.group == group_index:
            return membership.weight
    return 0.0


def add_target(mesh_obj, name: str, deform):
    key = mesh_obj.shape_key_add(name=name, from_mix=False)
    basis = mesh_obj.data.shape_keys.key_blocks["Basis"]
    moved = 0
    for vertex in mesh_obj.data.vertices:
        delta = deform(vertex, basis.data[vertex.index].co)
        if delta is not None:
            key.data[vertex.index].co += Vector(delta)
            moved += 1
    return moved


def rig_mesh(mesh_obj):
    if "Head" not in mesh_obj.vertex_groups:
        return False
    head_index = mesh_obj.vertex_groups["Head"].index
    head_vertices = [
        vertex for vertex in mesh_obj.data.vertices if weight(vertex, head_index) >= 0.10
    ]
    if not head_vertices:
        return False

    mins = [min(vertex.co[axis] for vertex in head_vertices) for axis in range(3)]
    maxs = [max(vertex.co[axis] for vertex in head_vertices) for axis in range(3)]
    center_x = (mins[0] + maxs[0]) * 0.5
    width = maxs[0] - mins[0]
    depth = maxs[1] - mins[1]
    height = maxs[2] - mins[2]
    # All three normalized interview meshes face local -Y after the rigging pass.
    front_y = mins[1]

    if mesh_obj.data.shape_keys is None:
        mesh_obj.shape_key_add(name="Basis", from_mix=False)

    def face_coords(vertex, co):
        head = weight(vertex, head_index)
        if head < 0.10:
            return None
        nx = (co.x - center_x) / max(width * 0.5, 1e-5)
        nz = (co.z - mins[2]) / max(height, 1e-5)
        front = 1.0 - abs(co.y - front_y) / max(depth * 0.50, 1e-5)
        return nx, nz, max(0.0, min(1.0, front)) * head

    def mouth_mask(vertex, co, z0=0.15, z1=0.48, half_width=0.55):
        values = face_coords(vertex, co)
        if values is None:
            return None
        nx, nz, front = values
        if abs(nx) > half_width or not z0 <= nz <= z1 or front <= 0.0:
            return None
        horizontal = max(0.0, 1.0 - abs(nx) / half_width)
        vertical = max(0.0, 1.0 - abs(nz - (z0 + z1) * 0.5) / ((z1 - z0) * 0.5))
        return nx, nz, front * horizontal * vertical

    def brow_mask(vertex, co, side):
        values = face_coords(vertex, co)
        if values is None:
            return None
        nx, nz, front = values
        if not 0.60 <= nz <= 0.78 or front <= 0.0 or nx * side < 0.02 or abs(nx) > 0.62:
            return None
        return front * max(0.0, 1.0 - abs(abs(nx) - 0.28) / 0.34)

    def eye_mask(vertex, co, side):
        values = face_coords(vertex, co)
        if values is None:
            return None
        nx, nz, front = values
        if not 0.49 <= nz <= 0.66 or front <= 0.0 or nx * side < 0.02 or abs(nx) > 0.62:
            return None
        return front * max(0.0, 1.0 - abs(abs(nx) - 0.28) / 0.34)

    def open_deform(scale):
        def deform(vertex, co):
            mask = mouth_mask(vertex, co)
            if mask is None:
                return None
            _, nz, amount = mask
            direction = -1.0 if nz < 0.335 else 0.35
            return (0.0, -0.004 * amount * scale, direction * 0.020 * amount * scale)
        return deform

    def wide_deform(vertex, co):
        mask = mouth_mask(vertex, co)
        if mask is None:
            return None
        nx, _, amount = mask
        return ((1.0 if nx >= 0 else -1.0) * 0.016 * amount, 0.0, 0.003 * amount)

    def tight_deform(vertex, co):
        mask = mouth_mask(vertex, co)
        if mask is None:
            return None
        nx, _, amount = mask
        return (-(1.0 if nx >= 0 else -1.0) * 0.012 * amount, -0.010 * amount, 0.0)

    def close_deform(vertex, co):
        mask = mouth_mask(vertex, co)
        if mask is None:
            return None
        _, nz, amount = mask
        return (0.0, 0.002 * amount, (1.0 if nz < 0.335 else -1.0) * 0.006 * amount)

    if add_target(mesh_obj, "V_Open", open_deform(1.0)) == 0:
        raise RuntimeError(f"Could not locate facial surface on {mesh_obj.name}")
    add_target(mesh_obj, "V_Explosive", close_deform)
    add_target(mesh_obj, "V_Dental_Lip", open_deform(0.30))
    add_target(mesh_obj, "V_Tight_O", tight_deform)
    add_target(mesh_obj, "V_Tight", tight_deform)
    add_target(mesh_obj, "V_Wide", wide_deform)
    add_target(mesh_obj, "V_Affricate", open_deform(0.45))
    add_target(mesh_obj, "V_Lip_Open", open_deform(0.65))
    add_target(mesh_obj, "Mouth_Drop_Lower", open_deform(0.90))
    add_target(mesh_obj, "Mouth_Contract", tight_deform)
    add_target(mesh_obj, "Mouth_Close", close_deform)
    add_target(mesh_obj, "Jaw_Open", open_deform(1.10))

    for side, suffix in ((-1, "L"), (1, "R")):
        def brow(up, side=side):
            def deform(vertex, co):
                amount = brow_mask(vertex, co, side)
                return None if amount is None else (0.0, 0.0, up * 0.014 * amount)
            return deform

        def squint(vertex, co, side=side):
            amount = eye_mask(vertex, co, side)
            return None if amount is None else (0.0, 0.002 * amount, -0.007 * amount)

        def smile(frown=False, side=side):
            def deform(vertex, co):
                mask = mouth_mask(vertex, co, 0.24, 0.46, 0.55)
                if mask is None:
                    return None
                nx, _, amount = mask
                if nx * side < 0.05:
                    return None
                sign = -1.0 if frown else 1.0
                return (side * 0.008 * amount, 0.0, sign * 0.014 * amount)
            return deform

        add_target(mesh_obj, f"Brow_Raise_Inner_{suffix}", brow(1.0))
        add_target(mesh_obj, f"Brow_Raise_Outer_{suffix}", brow(0.75))
        add_target(mesh_obj, f"Brow_Drop_{suffix}", brow(-0.75))
        add_target(mesh_obj, f"Eye_Squint_{suffix}", squint)
        add_target(mesh_obj, f"Mouth_Smile_{suffix}", smile(False))
        add_target(mesh_obj, f"Mouth_Frown_{suffix}", smile(True))
        add_target(mesh_obj, f"Cheek_Raise_{suffix}", brow(0.35))
    return True


def main():
    source, destination = arguments()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(source))
    rigged = [obj for obj in bpy.context.scene.objects if obj.type == "MESH" and rig_mesh(obj)]
    if not rigged:
        raise RuntimeError(f"No Head-weighted mesh found in {source}")

    for obj in list(bpy.context.scene.objects):
        if obj.type in {"CAMERA", "LIGHT"} or (obj.type == "MESH" and obj not in rigged and not obj.parent):
            bpy.data.objects.remove(obj, do_unlink=True)

    destination.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(destination),
        use_selection=False,
        add_leaf_bones=False,
        bake_anim=False,
        mesh_smooth_type="FACE",
        path_mode="AUTO",
    )
    print(f"CONVAI_FACE_RIG_OK {destination} targets={len(rigged[0].data.shape_keys.key_blocks)}")


if __name__ == "__main__":
    main()
