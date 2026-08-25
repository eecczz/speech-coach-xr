"""Create a Unity Humanoid-compatible armature for the three interview scans.

The source GLBs remain untouched.  A decimated, auto-weighted FBX and an
inspection .blend are generated from each source.

Usage:
  blender --background --python rig_sketchfab_humanoid.py -- \
      input.glb output.fbx preset report.json output.blend

Presets: female_tablet, businessman_folder, corporate_walk
"""

import json
import math
import sys
import traceback
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector


TARGET_TRIANGLES = 95_000


def command_args():
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    if len(values) != 5:
        raise SystemExit("Expected: input.glb output.fbx preset report.json output.blend")
    return Path(values[0]).resolve(), Path(values[1]).resolve(), values[2], Path(values[3]).resolve(), Path(values[4]).resolve()


def world_bounds(objects):
    corners = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    if not corners:
        raise RuntimeError("No mesh bounds were found")
    minimum = Vector((min(v.x for v in corners), min(v.y for v in corners), min(v.z for v in corners)))
    maximum = Vector((max(v.x for v in corners), max(v.y for v in corners), max(v.z for v in corners)))
    return minimum, maximum


def triangle_count(mesh_object):
    return sum(len(poly.vertices) - 2 for poly in mesh_object.data.polygons)


def prepare_mesh():
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not meshes:
        raise RuntimeError("The GLB contains no mesh")

    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    mesh = bpy.context.view_layer.objects.active
    mesh.name = "InterviewCharacter_Mesh"
    mesh.data.name = "InterviewCharacter_MeshData"

    # Detach from glTF transform nodes while keeping the visible world pose.
    world_matrix = mesh.matrix_world.copy()
    mesh.parent = None
    mesh.matrix_world = world_matrix
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

    before = triangle_count(mesh)
    if before > TARGET_TRIANGLES:
        modifier = mesh.modifiers.new("VR_Performance_Decimate", "DECIMATE")
        modifier.ratio = TARGET_TRIANGLES / before
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)

    # Remove unused glTF hierarchy nodes so FBX contains one clean character root.
    for obj in list(bpy.context.scene.objects):
        if obj != mesh and obj.type != "ARMATURE":
            bpy.data.objects.remove(obj, do_unlink=True)
    return mesh, before, triangle_count(mesh)


def normalized_landmarks(preset, minimum, maximum):
    height = maximum.z - minimum.z
    cx = (minimum.x + maximum.x) * 0.5
    cy = (minimum.y + maximum.y) * 0.5

    def p(x, y, z):
        return Vector((cx + x * height, cy + y * height, minimum.z + z * height))

    points = {
        "root": p(0.0, 0.0, 0.015),
        "hips": p(0.0, 0.0, 0.505),
        "spine": p(0.0, 0.0, 0.575),
        "chest": p(0.0, 0.0, 0.665),
        "upper_chest": p(0.0, 0.0, 0.745),
        "neck": p(0.0, 0.0, 0.825),
        "head": p(0.0, -0.004, 0.865),
        "head_top": p(0.0, 0.0, 0.975),
        "left_hip": p(0.055, 0.0, 0.50),
        "right_hip": p(-0.055, 0.0, 0.50),
        "left_knee": p(0.058, 0.0, 0.285),
        "right_knee": p(-0.058, 0.0, 0.285),
        "left_ankle": p(0.065, 0.0, 0.065),
        "right_ankle": p(-0.065, 0.0, 0.065),
        "left_foot": p(0.065, -0.016, 0.035),
        "right_foot": p(-0.065, -0.016, 0.035),
        "left_toe": p(0.065, -0.105, 0.025),
        "right_toe": p(-0.065, -0.105, 0.025),
        "left_shoulder": p(0.045, 0.0, 0.745),
        "right_shoulder": p(-0.045, 0.0, 0.745),
        "left_upper_arm": p(0.118, 0.0, 0.735),
        "right_upper_arm": p(-0.118, 0.0, 0.735),
    }

    if preset == "female_tablet":
        points.update({
            "left_elbow": p(0.128, -0.025, 0.615),
            "left_wrist": p(0.035, -0.075, 0.565),
            "left_hand": p(-0.002, -0.085, 0.555),
            "right_elbow": p(-0.126, -0.018, 0.61),
            "right_wrist": p(-0.045, -0.075, 0.585),
            "right_hand": p(-0.005, -0.085, 0.565),
            "left_knee": p(0.06, -0.005, 0.285),
            "left_ankle": p(0.075, -0.012, 0.065),
            "right_knee": p(-0.045, 0.015, 0.285),
            "right_ankle": p(-0.09, 0.015, 0.065),
        })
    elif preset == "businessman_folder":
        points.update({
            "left_elbow": p(0.13, -0.005, 0.60),
            "left_wrist": p(0.06, -0.075, 0.545),
            "left_hand": p(0.025, -0.09, 0.525),
            "right_elbow": p(-0.135, 0.0, 0.585),
            "right_wrist": p(-0.13, -0.008, 0.485),
            "right_hand": p(-0.13, -0.025, 0.435),
        })
    elif preset == "corporate_walk":
        points.update({
            "left_elbow": p(0.13, 0.01, 0.59),
            "left_wrist": p(0.135, -0.01, 0.485),
            "left_hand": p(0.14, -0.035, 0.44),
            "right_elbow": p(-0.13, -0.01, 0.60),
            "right_wrist": p(-0.135, -0.035, 0.50),
            "right_hand": p(-0.14, -0.055, 0.455),
            # Viewer-right/anatomical-left leg is lifted toward the camera.
            "left_knee": p(0.07, -0.12, 0.30),
            "left_ankle": p(0.06, -0.22, 0.105),
            "left_foot": p(0.06, -0.25, 0.075),
            "left_toe": p(0.06, -0.32, 0.07),
            "right_knee": p(-0.055, 0.015, 0.285),
            "right_ankle": p(-0.055, 0.015, 0.06),
        })
    else:
        raise ValueError(f"Unknown pose preset: {preset}")
    return points


def make_armature(points):
    armature_data = bpy.data.armatures.new("InterviewHumanoid_Armature")
    armature = bpy.data.objects.new("InterviewHumanoid", armature_data)
    bpy.context.collection.objects.link(armature)
    armature.show_in_front = True

    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bones = {}

    def bone(name, head, tail, parent=None, connected=False, deform=True):
        value = armature_data.edit_bones.new(name)
        value.head = points[head] if isinstance(head, str) else head
        value.tail = points[tail] if isinstance(tail, str) else tail
        value.use_deform = deform
        if parent:
            value.parent = bones[parent]
            value.use_connect = connected
        try:
            value.align_roll(Vector((0.0, -1.0, 0.0)))
        except RuntimeError:
            pass
        bones[name] = value
        return value

    bone("Root", "root", "hips", deform=False)
    bone("Hips", "hips", "spine", "Root")
    bone("Spine", "spine", "chest", "Hips", connected=True)
    bone("Chest", "chest", "upper_chest", "Spine", connected=True)
    bone("UpperChest", "upper_chest", "neck", "Chest", connected=True)
    bone("Neck", "neck", "head", "UpperChest", connected=True)
    bone("Head", "head", "head_top", "Neck", connected=True)

    for side in ("Left", "Right"):
        key = side.lower()
        bone(f"{side}UpperLeg", f"{key}_hip", f"{key}_knee", "Hips")
        bone(f"{side}LowerLeg", f"{key}_knee", f"{key}_ankle", f"{side}UpperLeg", connected=True)
        bone(f"{side}Foot", f"{key}_ankle", f"{key}_toe", f"{side}LowerLeg")
        toe_tail = points[f"{key}_toe"] + Vector((0.0, -0.045, 0.0))
        bone(f"{side}Toes", f"{key}_toe", toe_tail, f"{side}Foot", connected=True)

        bone(f"{side}Shoulder", f"{key}_shoulder", f"{key}_upper_arm", "UpperChest")
        bone(f"{side}UpperArm", f"{key}_upper_arm", f"{key}_elbow", f"{side}Shoulder", connected=True)
        bone(f"{side}LowerArm", f"{key}_elbow", f"{key}_wrist", f"{side}UpperArm", connected=True)
        bone(f"{side}Hand", f"{key}_wrist", f"{key}_hand", f"{side}LowerArm", connected=True)

    bpy.ops.object.mode_set(mode="OBJECT")
    return armature


def bind_with_automatic_weights(mesh, armature):
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    modifier = next((m for m in mesh.modifiers if m.type == "ARMATURE"), None)
    if not modifier:
        raise RuntimeError("Automatic weighting did not create an Armature modifier")
    modifier.name = "InterviewHumanoid_Deform"
    return modifier


def vertex_group_assignment_counts(mesh, names):
    index_to_name = {group.index: group.name for group in mesh.vertex_groups if group.name in names}
    counts = {name: 0 for name in names}
    for vertex in mesh.data.vertices:
        for membership in vertex.groups:
            name = index_to_name.get(membership.group)
            if name and membership.weight > 0.0001:
                counts[name] += 1
    return counts


def unweighted_vertex_count(mesh):
    return sum(
        1 for vertex in mesh.data.vertices
        if not any(membership.weight > 0.0001 for membership in vertex.groups)
    )


def assign_proximity_skin_weights(mesh, armature, minimum, maximum):
    """Deterministic fallback for dense/non-manifold photogrammetry scans.

    Blender's heat solver commonly refuses these single-piece scans.  This binds
    every vertex to its nearest anatomical bone segments with smooth four-bone
    blending, so the Unity Humanoid is usable instead of merely containing an
    empty Armature modifier.
    """
    deform_bones = [bone for bone in armature.data.bones if bone.use_deform]
    mesh.vertex_groups.clear()
    groups = {bone.name: mesh.vertex_groups.new(name=bone.name) for bone in deform_bones}
    group_indices = {name: group.index for name, group in groups.items()}
    height = maximum.z - minimum.z
    armature_matrix = armature.matrix_world
    mesh_matrix = mesh.matrix_world

    segments = []
    for bone in deform_bones:
        head = armature_matrix @ bone.head_local
        tail = armature_matrix @ bone.tail_local
        delta = tail - head
        length_squared = max(delta.length_squared, 1e-8)
        if bone.name in {"Hips", "Spine", "Chest", "UpperChest"}:
            radius = height * 0.12
            region = "torso"
        elif bone.name in {"Neck", "Head"}:
            radius = height * (0.12 if bone.name == "Head" else 0.075)
            region = "head"
        elif "Leg" in bone.name or "Foot" in bone.name or "Toes" in bone.name:
            radius = height * 0.065
            region = "leg"
        else:
            radius = height * 0.055
            region = "arm"
        segments.append((bone.name, head, delta, length_squared, radius, region))

    bm = bmesh.new()
    bm.from_mesh(mesh.data)
    bm.verts.ensure_lookup_table()
    deform_layer = bm.verts.layers.deform.verify()
    for vertex in bm.verts:
        world = mesh_matrix @ vertex.co
        z_norm = (world.z - minimum.z) / height
        scored = []
        for name, head, delta, length_squared, radius, region in segments:
            projection = max(0.0, min(1.0, (world - head).dot(delta) / length_squared))
            closest = head + delta * projection
            distance_squared = (world - closest).length_squared
            normalized_distance = math.sqrt(distance_squared) / max(radius, 1e-5)
            score = 1.0 / ((normalized_distance + 0.12) ** 4)

            if region == "leg" and z_norm > 0.58:
                score *= 0.004
            elif region == "arm" and z_norm < 0.40:
                score *= 0.004
            elif region == "torso" and z_norm < 0.39:
                score *= 0.012
            elif region == "head" and z_norm < 0.75:
                score *= 0.003
            if name == "Head" and z_norm > 0.84:
                score *= 8.0
            scored.append((score, name))

        strongest = sorted(scored, reverse=True)[:4]
        total = sum(score for score, _ in strongest)
        weights = vertex[deform_layer]
        weights.clear()
        for score, name in strongest:
            weight = score / total if total > 1e-12 else 0.0
            if weight > 0.001:
                weights[group_indices[name]] = weight

    bm.to_mesh(mesh.data)
    bm.free()
    mesh.data.update()


def export_fbx(mesh, armature, output_path):
    output_path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.export_scene.fbx(
        filepath=str(output_path),
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        use_armature_deform_only=True,
        bake_anim=False,
        path_mode="COPY",
        embed_textures=True,
    )


def main():
    source, output, preset, report_path, blend_path = command_args()
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    blend_path.parent.mkdir(parents=True, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source))
    mesh, triangles_before, triangles_after = prepare_mesh()
    minimum, maximum = world_bounds([mesh])
    points = normalized_landmarks(preset, minimum, maximum)
    armature = make_armature(points)
    bind_with_automatic_weights(mesh, armature)

    deform_bones = [bone.name for bone in armature.data.bones if bone.use_deform]
    group_counts = vertex_group_assignment_counts(mesh, deform_bones)
    empty_groups = [name for name in deform_bones if group_counts.get(name, 0) == 0]
    unweighted_before_fallback = unweighted_vertex_count(mesh)
    weighting_method = "automatic_heat"
    if empty_groups or unweighted_before_fallback:
        print(f"SPEAKUPXR_WEIGHT_FALLBACK=proximity (empty_groups={len(empty_groups)}, unweighted_vertices={unweighted_before_fallback})")
        assign_proximity_skin_weights(mesh, armature, minimum, maximum)
        group_counts = vertex_group_assignment_counts(mesh, deform_bones)
        empty_groups = [name for name in deform_bones if group_counts.get(name, 0) == 0]
        weighting_method = "proximity_four_bone"
    if empty_groups:
        raise RuntimeError("Missing deform vertex groups: " + ", ".join(empty_groups))
    unweighted_after_fallback = unweighted_vertex_count(mesh)
    if unweighted_after_fallback:
        raise RuntimeError(f"Skinning left {unweighted_after_fallback} vertices without weights")

    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path))
    export_fbx(mesh, armature, output)

    report = {
        "source": str(source),
        "output": str(output),
        "preset": preset,
        "triangles_before": triangles_before,
        "triangles_after": triangles_after,
        "bones": [bone.name for bone in armature.data.bones],
        "deform_vertex_groups": len([name for name in deform_bones if name in mesh.vertex_groups]),
        "vertex_group_assignments": group_counts,
        "weighting_method": weighting_method,
        "unweighted_before_fallback": unweighted_before_fallback,
        "unweighted_after_fallback": unweighted_after_fallback,
        "fbx_bytes": output.stat().st_size,
        "blend": str(blend_path),
    }
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("SPEAKUPXR_RIG_RESULT=" + json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        raise
