"""
Four candidate marks for Core's package icon, rendered in the shared stone system.

Mockups, not production. The production run is own-profile\\tools\\icons_build.py, and the
mark that wins gets pasted into its `marks()` dispatcher - this file exists so four
silhouettes can be compared side by side without rewriting a committed PNG to look at one.

The plumbing is imported rather than copied: icons_build.py does its rendering at module
level, so importing it would render all thirteen mods. Everything above its `MODS = [` line
is helpers and is exec'd here instead, which keeps the plate, the lights, the camera and the
colours identical to the real thing by construction.

Each candidate has a genuinely different outline, because two marks that share a silhouette
are one design:

    keystone   a trapezoid mass       the wedge the span falls without
    door       an upright rectangle   the gate, barred
    joint      an X                   two timbers lapped and pegged
    ring       a circle               the band the suite is held by

    blender --background --python mark_designs.py

Renders go to the scratchpad, at 256 and again at 64. 64 is the size that decides it: the
Thunderstore grid is where these are actually seen, and the current arch loses its five
wedges to one grey band there.
"""

import io
import math
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(ROOT, "own-profile", "tools", "icons_build.py")

OUT = os.environ.get("MARK_OUT") or os.path.join(HERE, "renders")


# ------------------------------------------------------------------ borrowed plumbing

_text = io.open(SOURCE, encoding="utf-8").read()
_helpers = _text.split("MODS = [")[0]

_ns = {"__name__": "icons_build_helpers", "__file__": SOURCE}
exec(compile(_helpers, SOURCE, "exec"), _ns)

STONE = _ns["STONE"]
clear, material, setup = _ns["clear"], _ns["material"], _ns["setup"]
poly, rect, oval = _ns["poly"], _ns["rect"], _ns["oval"]
move, spin, rounded = _ns["move"], _ns["spin"], _ns["rounded"]


def ring(cx, cy, radius, thick, mat, depth):
    """
    An annulus, which poly() cannot express - from_pydata builds one face and a face has no
    hole. A flattened torus is the cheap way to a band that is genuinely open in the middle,
    and open is the whole point: a filled disc is a coin, not a ring.
    """
    bpy.ops.mesh.primitive_torus_add(
        major_radius=radius, minor_radius=thick,
        major_segments=41, minor_segments=9,       # odd, per the note on oval()
        location=(cx, cy, 0.0))
    obj = bpy.context.object
    obj.scale = (1.0, 1.0, depth / max(thick, 1e-6) * 0.5)
    obj.data.materials.append(mat)
    return obj


# ------------------------------------------------------------------ the candidates

def keystone(dark, hot, pale, d):
    """
    One wedge, at the size the argument deserves.

    The arch on the current icon says the same thing with five wedges and a pair of piers,
    and at 64px the five become one band. So this drops the span entirely and keeps the part
    that carries the meaning: the stone the rest leans on, with the two it holds up leaning
    visibly onto it.
    """
    for side in (-1.0, 1.0):
        poly([(side * 0.88, -0.62), (side * 0.30, -0.62),
              (side * 0.42, 0.60), (side * 0.88, 0.60)], dark, depth=d)

    poly([(-0.26, -0.66), (0.26, -0.66), (0.40, 0.64), (-0.40, 0.64)], hot,
         z=d * 0.35, depth=d * 1.15)


def door(dark, hot, pale, d):
    """
    The gate, barred. Core's most visible job is turning someone away at the door, and a
    dropped bar is the one object that reads as refusal without a symbol to learn.

    Three planks rather than a slab, because a slab at this scale is a rectangle of nothing.
    """
    poly(rounded(-0.62, -0.80, 0.62, 0.70, 0.10), dark, depth=d)

    for x in (-0.21, 0.21):
        poly(rect(x - 0.015, -0.74, x + 0.015, 0.64), pale, z=d * 0.55, depth=d * 0.5)

    for side in (-1.0, 1.0):
        poly(rect(side * 0.56, -0.26, side * 0.86, 0.16), dark, z=d * 0.7, depth=d * 1.2)

    poly(rect(-0.80, -0.14, 0.80, 0.06), hot, z=d * 0.95, depth=d * 1.1)


def joint(dark, hot, pale, d):
    """
    Two timbers lapped and pegged. Shared plumbing drawn as the thing that actually joins
    two pieces, and the peg is the mod: pull it and the frame is two sticks.
    """
    for deg in (34.0, -34.0):
        poly(spin(rect(-0.92, -0.15, 0.92, 0.15), deg), dark, depth=d)

    poly(oval(0.0, 0.0, 0.27, 0.27, n=15), hot, z=d * 0.8, depth=d * 1.3)


def band(dark, hot, pale, d):
    """
    A ring, cut and clasped. The suite held in one band, and the clasp is where it is held.

    A circle is the outline nothing else in the family uses, which is most of why it is
    here: Rist's runestone is round but ragged, and at 64px ragged and true read apart.
    """
    ring(0.0, 0.0, 0.62, 0.155, dark, d * 1.6)
    poly(rect(-0.13, 0.42, 0.13, 0.86), hot, z=d * 0.9, depth=d * 1.2)
    poly(rect(-0.30, 0.60, 0.30, 0.72), dark, z=d * 1.1, depth=d * 1.3)


# ------------------------------------------------------------------ round two: the band

def arc(cx, cy, radius, thick, a0, a1, mat, depth, seg=41):
    """
    An open arc, as one strip of quads.

    A torus cannot be cut, and a C as one n-gon is a non-convex face that solidify and bevel
    both handle badly. A strip of quads is neither clever nor expensive, and each quad is
    convex.

    Two things that had to be paid for once. **The quads must not overlap**: a first version
    ran them a third of a step long so no hairline could open between neighbours, and every
    corner that buys lands outside the radius, so the ring came out serrated and read as a
    cog. Coplanar quads sharing an exact edge show no seam anyway. **And the per-quad bevel
    goes**, because a bevel on forty separate pieces draws forty highlight lines around a
    shape that is meant to be one band; the outer and inner rims are handled by the strip's
    own solidify.
    """
    step = (a1 - a0) / seg
    inner, outer = radius - thick, radius + thick
    for i in range(seg):
        b0 = math.radians(a0 + step * i)
        b1 = math.radians(a0 + step * (i + 1))
        poly([(cx + inner * math.cos(b0), cy + inner * math.sin(b0)),
              (cx + outer * math.cos(b0), cy + outer * math.sin(b0)),
              (cx + outer * math.cos(b1), cy + outer * math.sin(b1)),
              (cx + inner * math.cos(b1), cy + inner * math.sin(b1))],
             mat, depth=depth, bevel=0.0)


def band_clasp(dark, hot, pale, d):
    """
    The ring whole, with one collar holding it. Round one drew this and got two orange
    squares, because the dark cap sat on top of the accent and cut it in half rather than
    beside it. The collar is one piece now and the ring passes behind it.
    """
    ring(0.0, 0.0, 0.60, 0.16, dark, d * 1.6)
    poly(rounded(-0.26, 0.52, 0.26, 0.88, 0.06), hot, z=d * 0.9, depth=d * 1.4)


def band_key(dark, hot, pale, d):
    """
    The ring cut, and the accent is the piece that closes it.

    This is the keystone argument in a circle: the band is open without that one wedge, and
    the wedge is the mod. Of the three it is the only one where the accent is load-bearing
    rather than decorative, which is the whole reason to prefer it.
    """
    arc(0.0, 0.0, 0.60, 0.16, 104.0, 436.0, dark, d * 1.5)
    poly([(-0.19, 0.40), (0.19, 0.40), (0.26, 0.84), (-0.26, 0.84)], hot,
         z=d * 0.5, depth=d * 1.5)


def band_peg(dark, hot, pale, d):
    """
    The ring whole and riveted. The quietest of the three: the accent is a dot, so at 64px
    this is a dark circle with a spark on it and the ring carries the silhouette alone.
    """
    ring(0.0, 0.0, 0.60, 0.16, dark, d * 1.6)
    poly(rect(-0.30, 0.46, 0.30, 0.74), dark, z=d * 1.1, depth=d * 1.8)
    poly(oval(0.0, 0.60, 0.13, 0.13, n=15), hot, z=d * 1.6, depth=d * 1.6)


CANDIDATES = [
    ("keystone", keystone),
    ("door", door),
    ("joint", joint),
    ("band", band),
    ("band_clasp", band_clasp),
    ("band_key", band_key),
    ("band_peg", band_peg),
]


# ------------------------------------------------------------------ run

wanted = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if wanted:
    CANDIDATES = [c for c in CANDIDATES if c[0] in wanted]

if not os.path.isdir(OUT):
    os.makedirs(OUT)

for name, build in CANDIDATES:
    clear()
    setup()

    plate = material("plate", STONE["plate"], rough=0.78)
    poly(rounded(-1.0, -1.0, 1.0, 1.0, 0.16), plate, z=-0.16, depth=0.14, bevel=0.02)

    build(material("mark", STONE["mark"]),
          material("accent", STONE["accent"]),
          material("pale", STONE["pale"]),
          STONE["relief"])

    scene = bpy.context.scene
    for size in (256, 64):
        scene.render.resolution_x = scene.render.resolution_y = size
        scene.render.filepath = os.path.join(OUT, "%s_%d.png" % (name, size))
        bpy.ops.render.render(write_still=True)
        print("WROTE %s" % scene.render.filepath)

print("DONE")
