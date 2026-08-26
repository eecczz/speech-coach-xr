"""Render a geometry-only facial target preview for visual QA."""

import sys
from pathlib import Path

import bpy
from mathutils import Vector


source = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
output = Path(sys.argv[sys.argv.index("--") + 2]).resolve()
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(source))
mesh = next(obj for obj in bpy.context.scene.objects if obj.type == "MESH" and "Head" in obj.vertex_groups)
head_group = mesh.vertex_groups["Head"].index
vertices = [v for v in mesh.data.vertices if any(g.group == head_group and g.weight > 0.1 for g in v.groups)]
mins = Vector(tuple(min(v.co[i] for v in vertices) for i in range(3)))
maxs = Vector(tuple(max(v.co[i] for v in vertices) for i in range(3)))
depth = maxs.y - mins.y
front_sign = -1
center = mesh.matrix_world @ Vector(((mins.x + maxs.x) * 0.5, (mins.y + maxs.y) * 0.5, mins.z + (maxs.z - mins.z) * 0.47))

if mesh.data.shape_keys:
    for name, value in (("V_Open", 0.8), ("Mouth_Smile_L", 0.35), ("Mouth_Smile_R", 0.35)):
        if name in mesh.data.shape_keys.key_blocks:
            mesh.data.shape_keys.key_blocks[name].value = value

bpy.ops.object.camera_add(location=center + Vector((0, front_sign * 1.15, 0.03)))
camera = bpy.context.object
camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
camera.data.type = "ORTHO"
camera.data.ortho_scale = (maxs.z - mins.z) * 1.25
bpy.context.scene.camera = camera
bpy.context.scene.render.engine = "BLENDER_WORKBENCH"
bpy.context.scene.display.shading.light = "STUDIO"
bpy.context.scene.display.shading.color_type = "MATERIAL"
bpy.context.scene.render.resolution_x = 512
bpy.context.scene.render.resolution_y = 512
bpy.context.scene.render.resolution_percentage = 100
bpy.context.scene.render.image_settings.file_format = "PNG"
bpy.context.scene.render.filepath = str(output)
bpy.context.scene.render.film_transparent = False
bpy.context.scene.world = bpy.data.worlds.new("Preview World")
bpy.context.scene.world.color = (0.05, 0.05, 0.05)
bpy.ops.render.render(write_still=True)
print(f"FACE_PREVIEW_OK {output}")
