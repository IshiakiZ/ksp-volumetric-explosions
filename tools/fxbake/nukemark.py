"""nukemark.py OUT_FOLDER [--preview]: the ground under a nuclear explosion, for Volumetric Explosions (scorchwide.png, 1024 x
1024, ordinary colour and opacity; emberswide.png, 512 x 512, what still glows in it while fresh). Laid by Air.LayNuke through
Scorch (Air.cs) six and a half of the fireball's radii each way, so the picture's half-width is six and a half radii.

The blast marks of tools/fxbake's Burn are a shell's: a lopsided blot with tongues and dashes, some metres across. Enlarged to
hundreds of metres that reads as an ink splash. What the photographs of real ground zeros show from the air (looked at
2026-11-09: Trinity the day after, the Nevada tower and surface shots) is another thing: nearly round; a dark middle about the
fireball's own width where the ground was fused and charred, pale ash in patches over it; round that a broad scorched and
scoured zone a few radii out, densest inward, everything in it combed into fine streaks pointing away from the middle by the
blast; and beyond, a thin veil of singed ground that ends raggedly, some streaks reaching much further than others. On grass
(the Space Center's) the burn is dark: black where thick, brown where thin.

Python and numpy here, not fxbake's C#: this is made on the Windows PC, which has no .NET SDK. The pictures are committed in
src/VolumetricExplosions/Textures, as fxbake's are.

    python tools/fxbake/nukemark.py src/VolumetricExplosions/Textures [--preview]
"""
import os
import sys

import numpy as np
from PIL import Image

TAU = 2.0 * np.pi
rng = np.random.RandomState(7109)
TABLES = [rng.rand(256, 256).astype(np.float32) for _ in range(24)]


def smooth(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def value(x, y, k, period=0):
    """Smooth noise 0 to 1 at (x, y), table k; with a period, it repeats every that many of x (round the circle)."""
    t = TABLES[k]
    xi, yi = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
    fx, fy = x - xi, y - yi
    fx, fy = fx * fx * (3.0 - 2.0 * fx), fy * fy * (3.0 - 2.0 * fy)
    if period:
        x0, x1 = np.mod(xi, period) & 255, np.mod(xi + 1, period) & 255
    else:
        x0, x1 = xi & 255, (xi + 1) & 255
    y0, y1 = yi & 255, (yi + 1) & 255
    return (t[y0, x0] * (1 - fx) + t[y0, x1] * fx) * (1 - fy) + (t[y1, x0] * (1 - fx) + t[y1, x1] * fx) * fy


def fbm(x, y, k, octaves=4):
    total, weight, norm = 0.0, 1.0, 0.0
    for o in range(octaves):
        total = total + weight * value(x * (1 << o) + 11.3 * o, y * (1 << o) + 5.7 * o, k + o)
        norm += weight
        weight *= 0.5
    return total / norm


class Ground:
    """The parts of the mark at every pixel of a picture `size` across."""

    def __init__(self, size):
        yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
        u, v = (xx + 0.5) / size * 2 - 1, (yy + 0.5) / size * 2 - 1
        self.u, self.v = u, v
        r = np.hypot(u, v)
        round_ = (np.arctan2(v, u) + np.pi) / TAU                       # 0 to 1 round the circle
        self.r = r
        # Nearly round, never quite: the whole thing a little further out one way than another, and its edges wander.
        lobes = 0.6 * value(round_ * 5, r * 0 + 0.3, 0, 5) + 0.4 * value(round_ * 13, r * 0 + 1.7, 1, 13)
        low = fbm(u * 2.2 + 7, v * 2.2 + 3, 2, 3)
        rr = r / (1 + 0.34 * (lobes - 0.5) + 0.22 * (low - 0.5))
        self.rr = rr
        # The middle: fused and charred out to about the fireball's own reach (0.155 of the half-width), its edge ragged.
        ragged = fbm(u * 14, v * 14, 5, 3)
        self.char = smooth((0.165 * (1 + 0.6 * (ragged - 0.5)) - rr) / 0.1)
        # The streaks everything outside it lies in, pointing away from the middle: broad, fine and very fine, each
        # changing slowly along its length.
        s1 = value(round_ * 90, r * 3.0, 8, 90)
        s2 = value(round_ * 240, r * 5.0 + 3, 9, 240)
        s3 = value(round_ * 600, r * 8.0 + 1, 10, 600)
        self.streak = smooth((0.45 * s1 + 0.35 * s2 + 0.3 * s3 - 0.05 - 0.28) / 0.44)
        broad = smooth((0.6 * value(round_ * 34, r * 1.5, 11, 34) + 0.4 * s1 - 0.25) / 0.5)
        # How far out it reaches that way: a few wide bays and tongues round the circle, the broad streaks on top of them.
        far = 0.55 * value(round_ * 7, r * 0 + 0.9, 16, 7) + 0.45 * broad
        patchy = np.clip(0.55 + 1.3 * (fbm(u * 5 - 3, v * 5 + 1, 12, 3) - 0.5), 0, 1)
        self.patchy = patchy
        # The scorched and scoured zone: densest against the middle, gone between half and four fifths of the way out. (The dust
        # a burst on the ground lays round itself was measured in the film at near four radii by its 20th second and near six
        # by a minute, and lies for minutes: the scorched ground shows round it in the first quarter minute, as that of the
        # second try, five radii each way, did not, and the whole of it once the dust has gone, which the stills of
        # 2026-11-09 had as the eleventh minute.) Seen bare from above then, the first of these was a black sea urchin: as
        # dark at two radii as at one, every streak a separate black spine ending at the same distance. So it thins outward
        # from the start, ends nearer in some directions than others, and the streaks are in where it ends more than in how
        # dark it is: burnt ground is one sheet, feathered at its edge.
        self.zone = 1 - smooth((rr - 0.1) / ((0.56 + 0.26 * far) * (0.9 + 0.1 * self.streak) - 0.1))
        self.ring = 0.86 * self.zone * (0.86 + 0.14 * self.streak) * (0.72 + 0.28 * patchy)
        # The veil of singed ground beyond: a thin brown halo with the streaks in it, ending where it will (at 0.6 one way,
        # near the edge another). (As separate spines, the second of these, it was the sea urchin's.)
        reach = 0.6 + 0.38 * far
        self.veil = 0.42 * (1 - smooth((rr - 0.3) / (reach * (0.86 + 0.14 * self.streak) - 0.3))) * (0.82 + 0.18 * self.streak) * (0.6 + 0.4 * patchy)
        # Here and there in the zone, a streak burnt blacker (what was thrown down burning): drawn out along the rest,
        # never a round blot (those read as confetti at this size).
        spots = smooth((value(round_ * 70, rr * 7.0, 15, 70) - 0.72) / 0.14)
        self.spots = 0.25 * spots * smooth((0.75 - rr) / 0.3) * smooth((rr - 0.16) / 0.1)
        self.grit = fbm(u * 70, v * 70, 17, 2)

    def mark(self):
        soot = 1 - (1 - 0.96 * self.char) * (1 - self.ring) * (1 - self.veil) * (1 - self.spots)
        soot = soot * (0.86 + 0.28 * (self.grit - 0.5))
        thick = np.clip(soot * 1.2, 0, 1)
        # Black where the soot lies thick, brown where thin (as a smoke stain is, and singed grass); the thick of it a
        # little warmer in patches (baked earth).
        thin = np.array([0.215, 0.158, 0.102], np.float32)
        dark = np.array([0.040, 0.034, 0.030], np.float32)
        baked = np.array([0.095, 0.062, 0.042], np.float32)
        warm = (smooth((self.patchy - 0.35) / 0.4) * (1 - self.char))[..., None]
        deep = dark * (1 - 0.6 * warm) + baked * 0.6 * warm
        rgb = thin * (1 - thick[..., None]) + deep * thick[..., None]
        # Pale ash and fused ground in patches over the middle, most toward its centre.
        ash = self.char * smooth((fbm(self.u * 9 + 3, self.v * 9 - 2, 19, 3) - 0.44) / 0.24) * np.clip(1.15 - self.rr / 0.155, 0, 1) * 0.7
        rgb = rgb * (1 - ash[..., None]) + np.array([0.34, 0.32, 0.29], np.float32) * ash[..., None]
        a = np.clip(soot, 0, 1) * 0.94 * smooth((1 - self.r) / 0.08)
        return np.dstack([rgb, a])

    def embers(self):
        # Cracks and specks glowing in the middle, and bits still burning scattered through the zone round it.
        cracks = 1 - np.abs(2 * fbm(self.u * 18, self.v * 18, 20, 2) - 1)
        specks = smooth((value(self.u * 120, self.v * 120, 22) - 0.7) / 0.1)
        burning = smooth((value(self.u * 170 + 9, self.v * 170, 23) - 0.86) / 0.06) * smooth((self.patchy - 0.4) / 0.3)
        glow = self.char * (0.2 + 0.7 * cracks ** 6) + self.char * specks * 0.3 + 0.8 * self.zone * (1 - self.char) * burning
        glow = np.clip(glow, 0, 1) * smooth((1 - self.r) / 0.08)
        return np.dstack([np.ones_like(glow), 0.42 + 0.4 * glow, 0.08 + 0.25 * glow * glow, glow])


def save(path, picture):
    Image.fromarray((np.clip(picture, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA").save(path, optimize=True)
    print(path, picture.shape[1], "x", picture.shape[0], os.path.getsize(path), "bytes")


folder = sys.argv[1]
os.makedirs(folder, exist_ok=True)
mark = Ground(1024).mark()
save(os.path.join(folder, "scorchwide.png"), mark)
save(os.path.join(folder, "emberswide.png"), Ground(512).embers())
if "--preview" in sys.argv:
    # The mark over grass, sand and concrete side by side, to judge it without starting the game.
    grounds = [(0.42, 0.47, 0.17), (0.77, 0.70, 0.55), (0.59, 0.58, 0.55)]
    sheet = np.hstack([np.array(g, np.float32) * (1 - mark[..., 3:]) + mark[..., :3] * mark[..., 3:] for g in grounds])
    Image.fromarray((sheet * 255 + 0.5).astype(np.uint8), "RGB").save(os.path.join(folder, "preview_nukemark.jpg"), quality=90)
