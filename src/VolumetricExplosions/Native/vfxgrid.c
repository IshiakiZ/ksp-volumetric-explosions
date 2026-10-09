// The making of a grid, in C.
//
// Turning the particles into the grids the shader draws from (see Site.Lighting and the functions after it
// in Site.cs) is nearly all the processor time the mod takes: in the game's own runtime, seventy-odd
// milliseconds of work for every grid, twenty-five grids a second, which it had to spread over every core
// of the processor to keep up with. That in turn slowed the game's own threads by more than all the
// drawing did. The same sums in C take a few milliseconds on one thread.
//
// This follows the C# function for function and line for line: the same sums in the same order, so that
// what comes out is the same to within the last rounding (the game's runtime carries some sums at higher
// precision on the way; the development build can make a grid both ways and compare, see Site.NativeCheck).
// The C# stays as it was, and is what runs wherever this library is missing or will not load. WHATEVER IS
// CHANGED IN ONE MUST BE CHANGED IN THE OTHER.
//
// Built by build.sh into PluginData/vfxgrid.dylib (macOS), vfxgrid.so (Linux) or vfxgrid.dll (Windows, where
// build.sh has a compiler that can make one: see there). The particle's layout
// comes from Site.cs by way of tools/native/genp.py.
#include <limits.h>
#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "p.h"

#define G 64
#define G2 (G * G)
#define G3 (G * G * G)
#define LEVELS 4
#define A 32
#define A3 (A * A * A)
#define RECKONINGS 6
#define COUNTS (4 + 3 * RECKONINGS)
#define RATE (COUNTS + RECKONINGS)
#define CARRIED (RATE + 1)
#define BELL_STEPS 1024
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
// (Windows' own clock, asked for by name: none of its headers is needed for anything else)
__declspec(dllimport) int __stdcall QueryPerformanceCounter(int64_t *count);
__declspec(dllimport) int __stdcall QueryPerformanceFrequency(int64_t *frequency);
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// What a grid is to be made from, and where it is to go. (The same fields in the same order as Native.Grid in Native.cs.)
typedef struct Grid
{
    int32_t size, g, a, levels, carried;                 // of this description, and of the grids: checked against what this was built for
    int32_t count, drawn, sunUp, halves, floored;        // particles; whether the smoke is drawn as a volume; whether the sun is up; whether sixteen-bit numbers go as half-precision ones; whether there is ground
    const P *particles;
    float x0, y0, z0, dX, dY, dZ;                        // the corner of the grid and the size of its cells
    float keepX0, keepX1, keepY0, keepY1, keepZ0, keepZ1, fadeX, fadeY, fadeZ;
    float leastReach, thick, hold, goesX, goesY, goesZ, ahead;
    float sunX, sunY, sunZ, repeat;
    float skyX, skyY, skyZ;                              // which way is up, in the grid's own axes: as a rule its second, but the grid of an engine's trail is turned to lie along the trail
    float thickest, thickestFlame, something, restFar, mostBefore, mostAbove;
    float fastest;                                       // (comes back)
    float *sunThrough, *skyThrough;                      // G3 each
    float **carry;                                       // CARRIED grids of A3
    uint8_t *cellsA, *cellsB, *cellsC, *cellsD;          // four bytes a cell: G3, G3, A3, A3 cells
    uint16_t **cellsRest;                                // five grids of A3 cells, four numbers a cell
    uint8_t *cellsClear;                                 // A3
    const float *floors;                                 // the height of the ground under the middle of each column of the grid (G by G), then of each column of each coarser grid the widest puffs are put on (see Site.Floors)
    uint8_t *cellsE;                                     // four bytes a cell, A3 cells: whose turn it is there (the point of its round the smoke is at, for the big billows' three reckonings and for the small ones')
    float *strain;                                       // A3: (comes back) how fast the smoke is being pulled out of shape at each cell, per second
    const float *bells;                                  // BELL_STEPS numbers: how much a reckoning counts by how far through its life it is (see Site.BellAt)
    int64_t nanoseconds[8];                              // (comes back) what each part took: 1 the particles, 2 the sun, 3 the sky, 4 round about, 5 carry, 7 packing
} Grid;

// Room to work in, kept from one grid to the next: one for each site.
typedef struct Work
{
    float *field[5];                                     // thick, pale, dusty, flaming, hotness
    float *wide[LEVELS][5];
    float *spare0, *spare1;
    float *layer[2];
    float *anything, *roundSmoke, *roundPale, *roundDust;
    float *carried, *carriedTemp;                        // CARRIED numbers for each cell of the coarser grid, side by side (see carry)
    float line[A];
    float sums[CARRIED];
    float stand[G2];                                     // (see deposit: where each column of a puff lying on the ground stands, and which cells of it the puff reaches)
    int rows[2 * G];
    uint8_t *known, *clear;
    int wideUsed[LEVELS];
} Work;

static uint8_t rootByte[65536];
static int rootsMade;

static int64_t now_ns(void)
{
#ifdef _WIN32
    static int64_t every;
    int64_t count = 0;
    if (every == 0 && !QueryPerformanceFrequency(&every)) every = 1;
    QueryPerformanceCounter(&count);
    return (int64_t)((double)count * 1e9 / (double)every);
#else
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (int64_t)t.tv_sec * 1000000000 + t.tv_nsec;
#endif
}

// A whole number from a fraction the way the game's runtime makes one on this kind of processor: towards
// nought, and the lowest number there is for anything that will not fit (or is no number at all).
static inline int to_int(float v)
{
    return v > -2147483648.0f && v < 2147483648.0f ? (int)v : INT_MIN;
}

static inline float root_(float x)
{
    if (x <= 1e-30f) return 0.0f;
    union { float f; int32_t i; } b;
    b.f = x;
    b.i = 0x1fbd1df5 + (b.i >> 1);
    float y = b.f;
    y = 0.5f * (y + x / y);
    return 0.5f * (y + x / y);
}

#define THIRD (1.0f / 3.0f)

// How much a reckoning counts for at a point of its round (not less than nought): read from the game's own list of it.
static inline float bell_(const float *bells, float round)
{
    return bells[(int)(round * BELL_STEPS) & (BELL_STEPS - 1)];
}

EXPORT int vfx_signature(void) { return (int)P_SIGNATURE; }
EXPORT int vfx_size_of_p(void) { return (int)sizeof(P); }
EXPORT int vfx_size_of_grid(void) { return (int)sizeof(Grid); }

EXPORT void vfx_free(void *work)
{
    Work *w = (Work *)work;
    if (w == NULL) return;
    for (int c = 0; c < 5; c++) free(w->field[c]);
    for (int level = 1; level < LEVELS; level++) for (int c = 0; c < 5; c++) free(w->wide[level][c]);
    free(w->spare0); free(w->spare1); free(w->layer[0]); free(w->layer[1]);
    free(w->anything); free(w->roundSmoke); free(w->roundPale); free(w->roundDust); free(w->carried); free(w->carriedTemp);
    free(w->known); free(w->clear);
    free(w);
}

EXPORT void *vfx_new(void)
{
    Work *w = (Work *)calloc(1, sizeof(Work));
    if (w == NULL) return NULL;
    int ok = 1;
    for (int c = 0; c < 5; c++) ok &= (w->field[c] = (float *)calloc(G3, sizeof(float))) != NULL;
    for (int level = 1; level < LEVELS; level++)
    {
        int side = G >> level;
        for (int c = 0; c < 5; c++) ok &= (w->wide[level][c] = (float *)calloc((size_t)side * side * side, sizeof(float))) != NULL;
    }
    ok &= (w->spare0 = (float *)calloc(G * G / 2 * G / 2, sizeof(float))) != NULL;
    ok &= (w->spare1 = (float *)calloc(G * G * G / 2, sizeof(float))) != NULL;
    ok &= (w->layer[0] = (float *)calloc(G2, sizeof(float))) != NULL;
    ok &= (w->layer[1] = (float *)calloc(G2, sizeof(float))) != NULL;
    ok &= (w->anything = (float *)calloc(A3, sizeof(float))) != NULL;
    ok &= (w->roundSmoke = (float *)calloc(A3, sizeof(float))) != NULL;
    ok &= (w->roundPale = (float *)calloc(A3, sizeof(float))) != NULL;
    ok &= (w->roundDust = (float *)calloc(A3, sizeof(float))) != NULL;
    ok &= (w->carried = (float *)calloc((size_t)A3 * CARRIED, sizeof(float))) != NULL;
    ok &= (w->carriedTemp = (float *)calloc((size_t)A3 * CARRIED, sizeof(float))) != NULL;
    ok &= (w->known = (uint8_t *)calloc(A3, 1)) != NULL;
    ok &= (w->clear = (uint8_t *)calloc((A + 2) * (A + 2) * (A + 2), 1)) != NULL;
    if (!ok) { vfx_free(w); return NULL; }
    if (!rootsMade)
    {
        // (the square root of a share, 0 to 1 in 65536 steps, as a byte: see Site.Roots)
        for (int n = 0; n < 65536; n++) rootByte[n] = (uint8_t)((float)sqrt((double)(n / 65535.0f)) * 255.0f);
        rootsMade = 1;
    }
    return w;
}

// ---------------------------------------------------------------- the particles onto the grid (Site.Deposit)

static const float Narrowing[LEVELS] = { 1.5f, 1.5f * 5.0f, 1.5f * 21.0f, 1.5f * 85.0f };
static const int FloorsAt[LEVELS] = { 0, G2, G2 + G2 / 4, G2 + G2 / 4 + G2 / 16 };

static void deposit(Work *w, const Grid *g)
{
    const P *a = g->particles;
    int count = g->count;
    float x0 = g->x0, y0 = g->y0, z0 = g->z0, hx = g->dX, hy = g->dY, hz = g->dZ, least = g->leastReach;
    float widest = hx > (hy > hz ? hy : hz) ? hx : (hy > hz ? hy : hz), ihx = 1.0f / hx, ihz = 1.0f / hz;
    int drawn = g->drawn;
    const float *lieOf = g->floors;
    float *stand = w->stand;
    int *rowsOf = w->rows;
    for (int i = 0; i < count; i++)
    {
        float r = a[i].r, heat = a[i].heat, life = a[i].life, age = a[i].age;
        if (r <= 0.01f || life < 0.0f) continue;
        float fade = life - age < life * 0.22f ? (life - age) / (life * 0.22f) : 1.0f;
        if (a[i].fadeIn > 0.0f && age < a[i].fadeIn) fade *= age / a[i].fadeIn;
        float soot = heat > 0.5f ? 0.0f : heat < 0.12f ? 1.0f : (0.5f - heat) * 2.6316f;
        float tau = a[i].mass / (r * r);
        if (tau > 8.0f) tau = 8.0f;
        float smoke = tau / (2.0f * r) * soot * fade * g->thick;     // per metre, inside the particle
        float spread = a[i].born * 1.6f / r;                           // flame that has spread out is thinner
        float flame = a[i].flame * (heat < 0.02f ? 0.0f : heat > 0.3f ? 1.0f : (heat - 0.02f) * 3.5714f) * (a[i].mass > 0.0f ? 1.0f : fade) * 0.3f / r * (spread < 1.0f ? spread * spread : 1.0f);
        if (!drawn) flame = 0.0f;
        if (smoke < 1e-4f && flame < 1e-4f) continue;
        float px = a[i].x, py = a[i].y, pz = a[i].z;
        if (drawn)
        {
            float u = (px - g->keepX0 < g->keepX1 - px ? px - g->keepX0 : g->keepX1 - px) * g->fadeX, v = (py - g->keepY0 < g->keepY1 - py ? py - g->keepY0 : g->keepY1 - py) * g->fadeY,
                  w3 = (pz - g->keepZ0 < g->keepZ1 - pz ? pz - g->keepZ0 : g->keepZ1 - pz) * g->fadeZ;
            float share = u < v ? (u < w3 ? u : w3) : (v < w3 ? v : w3);
            if (share <= 0.0f) continue;
            if (share < 1.0f) { smoke *= share; flame *= share; }
        }
        float reach = 1.474f * r;
        if (reach < least) reach = least;
        // Smoke rests on the ground; none of it is under it: a ball that would reach below the ground is stood on
        // it, flatter and as much wider (see the C#).
        float wide = reach, tall = reach, ground = 0.0f, follows = 0.0f;
        int resting = 0;
        if (g->floored)
        {
            float gu = (px - x0) * ihx - 0.5f, gv = (pz - z0) * ihz - 0.5f;
            if (!(gu > 0.0f)) gu = 0.0f; else if (gu > G - 1.001f) gu = G - 1.001f;
            if (!(gv > 0.0f)) gv = 0.0f; else if (gv > G - 1.001f) gv = G - 1.001f;
            int gi = (int)gu, gk = (int)gv, gAt = gi + gk * G;
            float fu = gu - gi, fv = gv - gk;
            float lower = lieOf[gAt] + (lieOf[gAt + 1] - lieOf[gAt]) * fu, upper = lieOf[gAt + G] + (lieOf[gAt + G + 1] - lieOf[gAt + G]) * fu;
            ground = lower + (upper - lower) * fv;
            float height = py - ground;
            if (height < reach)
            {
                if (height < 0.0f) height = 0.0f;
                tall = 0.5f * (height + reach);
                wide = reach * root_(reach / tall);
                py = ground + tall;
                follows = 2.0f * (reach - height) / reach;
                if (follows > 1.0f) follows = 1.0f;
                resting = 1;
            }
        }
        int level = 0;
        float span = 4.5f * widest;
        while (level < LEVELS - 1 && wide > span) { level++; span *= 2.0f; }
        float times = (float)(1 << level), narrow = Narrowing[level];
        float lx = hx * times, ly = hy * times, lz = hz * times;
        float rx = wide * wide - narrow * hx * hx, ry = tall * tall - narrow * hy * hy, rz = wide * wide - narrow * hz * hz;
        float fx = 1.05f * lx, fy = 1.05f * ly, fz = 1.05f * lz;
        rx = rx > fx * fx ? root_(rx) : fx;
        ry = ry > fy * fy ? root_(ry) : fy;
        rz = rz > fz * fz ? root_(rz) : fz;
        if (resting && py - ry < ground) py = ground + ry;
        int side = G >> level;
        float ix = 1.0f / lx, iy = 1.0f / ly, iz = 1.0f / lz;
        int i0 = to_int((px - rx - x0) * ix), i1 = to_int((px + rx - x0) * ix), j0 = to_int((py - ry - y0) * iy), j1 = to_int((py + ry - y0) * iy), k0 = to_int((pz - rz - z0) * iz), k1 = to_int((pz + rz - z0) * iz);
        if (i0 < 0) i0 = 0; if (j0 < 0) j0 = 0; if (k0 < 0) k0 = 0;
        if (i1 > side - 1) i1 = side - 1; if (j1 > side - 1) j1 = side - 1; if (k1 > side - 1) k1 = side - 1;
        if (resting)
        {
            // Each column of it stands on the ground under that column: where the middle of the ball is in each
            // column it reaches, and which cells of that column it can reach (see the C#).
            int lieAt = FloorsAt[level];
            for (int k = k0; k <= k1; k++)
            {
                int lieRow = lieAt + k * side, standRow = k * side;
                float lowest = 0.0f, highest = 0.0f;
                for (int c = i0; c <= i1; c++)
                {
                    float shift = lieOf[lieRow + c] - ground;
                    if (shift > wide) shift = wide; else if (shift < -wide) shift = -wide;
                    shift *= follows;
                    if (shift < lowest) lowest = shift; else if (shift > highest) highest = shift;
                    stand[standRow + c] = py + shift;
                }
                int ja = to_int((py + lowest - ry - y0) * iy), jb = to_int((py + highest + ry - y0) * iy);
                if (ja < 0) ja = 0; if (jb > side - 1) jb = side - 1;
                rowsOf[2 * k] = ja; rowsOf[2 * k + 1] = jb;
            }
        }
        float qx = 1.0f / (rx * rx), qy = 1.0f / (ry * ry), qz = 1.0f / (rz * rz), total;
        // (How far each cell of a row is from the middle of the ball along the row, squared, over the ball's width
        // squared: the same for every row of the ball, so worked out once and not for every cell of every row.)
        float along[G];
        for (int c = i0; c <= i1; c++) { float dx = x0 + (c + 0.5f) * lx - px; along[c] = dx * dx * qx; }
        if (rx >= 3.2f * lx && ry >= 3.2f * ly && rz >= 3.2f * lz)
        {
            total = 0.95744f * rx * ry * rz * ix * iy * iz;
        }
        else if (!resting)
        {
            total = 0.0f;
            for (int k = k0; k <= k1; k++)
            {
                float dz = z0 + (k + 0.5f) * lz - pz, wz = 1.0f - dz * dz * qz;
                if (wz <= 0.0f) continue;
                for (int j = j0; j <= j1; j++)
                {
                    float dy = y0 + (j + 0.5f) * ly - py, wy = wz - dy * dy * qy;
                    if (wy <= 0.0f) continue;
                    for (int c = i0; c <= i1; c++)
                    {
                        float wt = wy - along[c];
                        if (wt > 0.0f) total += wt * wt;
                    }
                }
            }
            if (total <= 0.0f) continue;
        }
        else
        {
            total = 0.0f;
            for (int k = k0; k <= k1; k++)
            {
                float dz = z0 + (k + 0.5f) * lz - pz, wz = 1.0f - dz * dz * qz;
                if (wz <= 0.0f) continue;
                int standRow = k * side, jb = rowsOf[2 * k + 1];
                for (int j = rowsOf[2 * k]; j <= jb; j++)
                {
                    float y = y0 + (j + 0.5f) * ly;
                    for (int c = i0; c <= i1; c++)
                    {
                        float dy = y - stand[standRow + c], wt = wz - dy * dy * qy - along[c];
                        if (wt > 0.0f) total += wt * wt;
                    }
                }
            }
            if (total <= 0.0f) continue;
        }
        float worth = 4.19f * r * r * r * ix * iy * iz / total;
        float s = smoke * worth, f = flame * worth;
        float light = (a[i].ar + a[i].ag + a[i].ab) * 0.3333f;
        float warm = a[i].ar > 1e-3f ? (a[i].ar - a[i].ab) / a[i].ar * 2.5f : 0.0f;
        float sl = s * light, sd = s * (warm < 0.0f ? 0.0f : warm > 1.0f ? 1.0f : warm), fh = f * (heat > 1.2f ? 1.2f : heat);
        float **into = level == 0 ? w->field : w->wide[level];
        float *restrict smokeIn = into[0], *restrict paleIn = into[1], *restrict dustIn = into[2], *restrict flameIn = into[3], *restrict hotIn = into[4];
        if (level > 0) w->wideUsed[level] = 1;
        if (!resting)
        {
            for (int k = k0; k <= k1; k++)
            {
                float dz = z0 + (k + 0.5f) * lz - pz, wz = 1.0f - dz * dz * qz;
                if (wz <= 0.0f) continue;
                for (int j = j0; j <= j1; j++)
                {
                    float dy = y0 + (j + 0.5f) * ly - py, wy = wz - dy * dy * qy;
                    if (wy <= 0.0f) continue;
                    int row = (j + k * side) * side;
                    // (A cell outside the ball is given nothing, which leaves it as it was: the same as passing it over.)
                    if (s > 0.0f)
                        for (int c = i0; c <= i1; c++)
                        {
                            float wt = wy - along[c];
                            wt = wt > 0.0f ? wt * wt : 0.0f;
                            smokeIn[row + c] += s * wt; paleIn[row + c] += sl * wt; dustIn[row + c] += sd * wt;
                        }
                    if (f > 0.0f)
                        for (int c = i0; c <= i1; c++)
                        {
                            float wt = wy - along[c];
                            wt = wt > 0.0f ? wt * wt : 0.0f;
                            flameIn[row + c] += f * wt; hotIn[row + c] += fh * wt;
                        }
                }
            }
            continue;
        }
        for (int k = k0; k <= k1; k++)
        {
            float dz = z0 + (k + 0.5f) * lz - pz, wz = 1.0f - dz * dz * qz;
            if (wz <= 0.0f) continue;
            const float *standRow = stand + k * side;
            int jb = rowsOf[2 * k + 1];
            for (int j = rowsOf[2 * k]; j <= jb; j++)
            {
                float y = y0 + (j + 0.5f) * ly;
                int row = (j + k * side) * side;
                if (s > 0.0f)
                    for (int c = i0; c <= i1; c++)
                    {
                        float dy = y - standRow[c], wt = wz - dy * dy * qy - along[c];
                        wt = wt > 0.0f ? wt * wt : 0.0f;
                        smokeIn[row + c] += s * wt; paleIn[row + c] += sl * wt; dustIn[row + c] += sd * wt;
                    }
                if (f > 0.0f)
                    for (int c = i0; c <= i1; c++)
                    {
                        float dy = y - standRow[c], wt = wz - dy * dy * qy - along[c];
                        wt = wt > 0.0f ? wt * wt : 0.0f;
                        flameIn[row + c] += f * wt; hotIn[row + c] += fh * wt;
                    }
            }
        }
    }
}

// Add a grid of 'side' cells each way to one of twice as many (Site.Widen).
static void widen(const float *from, int side, float *into, float *first, float *second)
{
    int twice = side * 2;
    for (int k = 0; k < side; k++)
        for (int j = 0; j < side; j++)
        {
            int src = (j + k * side) * side, dst = (j + k * side) * twice;
            for (int i = 0; i < side; i++)
            {
                float c = from[src + i], below = i > 0 ? from[src + i - 1] : c, above = i < side - 1 ? from[src + i + 1] : c;
                first[dst + 2 * i] = 0.75f * c + 0.25f * below;
                first[dst + 2 * i + 1] = 0.75f * c + 0.25f * above;
            }
        }
    for (int k = 0; k < side; k++)
        for (int j = 0; j < side; j++)
        {
            int src = (j + k * side) * twice, below = (j > 0 ? src - twice : src), above = (j < side - 1 ? src + twice : src), dst = (2 * j + k * twice) * twice;
            for (int i = 0; i < twice; i++)
            {
                float c = first[src + i];
                second[dst + i] = 0.75f * c + 0.25f * first[below + i];
                second[dst + twice + i] = 0.75f * c + 0.25f * first[above + i];
            }
        }
    int layer = twice * twice;
    for (int k = 0; k < side; k++)
    {
        int src = k * layer, below = k > 0 ? src - layer : src, above = k < side - 1 ? src + layer : src, dst = 2 * k * layer;
        for (int i = 0; i < layer; i++)
        {
            float c = second[src + i];
            into[dst + i] += 0.75f * c + 0.25f * second[below + i];
            into[dst + layer + i] += 0.75f * c + 0.25f * second[above + i];
        }
    }
}

// Bring what was put on the coarser grids down to one of the fields (Site.Gather). Each coarser grid holds
// what the particles put on it and nothing else: they are all cleared before every grid.
static void gather(Work *w, int which)
{
    float *carried = NULL;
    for (int level = LEVELS - 1; level >= 1; level--)
    {
        if (!w->wideUsed[level] && carried == NULL) continue;
        int side = G >> level;
        float *here = w->wide[level][which];
        if (carried != NULL) widen(carried, side / 2, here, w->spare0, w->spare1);
        carried = here;
    }
    if (carried != NULL) widen(carried, G / 2, w->field[which], w->spare0, w->spare1);
}

// ---------------------------------------------------------------- light through the smoke (Site.Follow)

static void follow(Work *w, const Grid *g, float tx, float ty, float tz, float *through, float soften)
{
    float cellX = g->dX, cellY = g->dY, cellZ = g->dZ;
    const float *smokeIn = w->field[0];
    float *before = w->layer[0], *after = w->layer[1];
    float ax = fabsf(tx), ay = fabsf(ty), az = fabsf(tz);
    int sa, su, sv;
    float la, lu, lv, ca, cu, cv;
    if (ay >= ax && ay >= az) { sa = G; su = G2; sv = 1; la = ty; lu = tz; lv = tx; ca = cellY; cu = cellZ; cv = cellX; }
    else if (ax >= az) { sa = 1; su = G; sv = G2; la = tx; lu = ty; lv = tz; ca = cellX; cu = cellY; cv = cellZ; }
    else { sa = G2; su = 1; sv = G; la = tz; lu = tx; lv = ty; ca = cellZ; cu = cellX; cv = cellY; }
    float steep = fabsf(la) < 0.2f ? 0.2f : fabsf(la);
    int toward = la > 0.0f ? 1 : -1;
    float du = lu / steep * ca / cu, dv = lv / steep * ca / cv;
    float path = ca / steep * soften;
    int ou = to_int(floorf(du)), ov = to_int(floorf(dv));
    float wu = du - ou, wv = dv - ov;
    float w00 = (1.0f - wu) * (1.0f - wv), w10 = wu * (1.0f - wv), w01 = (1.0f - wu) * wv, w11 = wu * wv;
    int iLow = 0 > -ou ? 0 : -ou, iHigh = G - 1 < G - 2 - ou ? G - 1 : G - 2 - ou, jLow = 0 > -ov ? 0 : -ov, jHigh = G - 1 < G - 2 - ov ? G - 1 : G - 2 - ov, shift = ou + ov * G;
    for (int m = 0; m < G; m++)
    {
        int layer = toward > 0 ? G - 1 - m : m, start = layer * sa;
        // (Every cell of a layer is worked out from the layer before and from nothing else, so the order within
        // a layer is free: here it is whichever goes through memory in step. The C# always takes them row by
        // row, which for light from above or below jumps a whole layer of the grid at every cell.)
        if (sv == 1)
            for (int i = 0; i < G; i++)
            {
                int insideI = m > 0 && i >= iLow && i <= iHigh;
                for (int j = 0; j < G; j++)
                {
                    float arriving = 0.0f;
                    if (insideI && j >= jLow && j <= jHigh)
                    {
                        int b = j * G + i + shift;
                        arriving = before[b] * w00 + before[b + 1] * w10 + before[b + G] * w01 + before[b + G + 1] * w11;
                    }
                    int at = start + j + i * su;
                    float x = smokeIn[at] * path;
                    through[at] = arriving + 0.5f * x;
                    after[j * G + i] = arriving + x;
                }
            }
        else
            for (int j = 0; j < G; j++)
            {
                int inside = m > 0 && j >= jLow && j <= jHigh;
                int row = j * G, cells = start + j * sv;
                for (int i = 0; i < G; i++)
                {
                    float arriving = 0.0f;
                    if (inside && i >= iLow && i <= iHigh)
                    {
                        int b = row + i + shift;
                        arriving = before[b] * w00 + before[b + 1] * w10 + before[b + G] * w01 + before[b + G + 1] * w11;
                    }
                    int at = cells + i * su;
                    float x = smokeIn[at] * path;
                    through[at] = arriving + 0.5f * x;
                    after[row + i] = arriving + x;
                }
            }
        float *swap = before; before = after; after = swap;
    }
}

// ---------------------------------------------------------------- what is round about each place (Site.Around)

static void around_layer(Work *w, const Grid *g, int k)
{
    const float *smokeIn = w->field[0], *paleIn = w->field[1], *dustIn = w->field[2], *flameIn = w->field[3];
    float *most = w->anything, *smokes = w->roundSmoke, *pales = w->roundPale, *dusts = w->roundDust;
    const float flameCounts = g->thickest / g->thickestFlame;
    for (int j = 0; j < A; j++)
    {
        int row = (j + k * A) * A, under = (2 * j + 2 * k * G) * G;
        for (int i = 0; i < A; i++)
        {
            int at = under + 2 * i;
            float top = 0.0f, smoke = 0.0f, light = 0.0f, dust = 0.0f;
            for (int c = 0; c < 8; c++)
            {
                int cell = at + (c & 1) + ((c >> 1) & 1) * G + (c >> 2) * G2;
                float v = smokeIn[cell] + flameCounts * flameIn[cell];
                if (v > top) top = v;
                smoke += smokeIn[cell]; light += paleIn[cell]; dust += dustIn[cell];
            }
            most[row + i] = top;
            smokes[row + i] = smoke; pales[row + i] = light; dusts[row + i] = dust;
        }
    }
}

// Smooth one of the coarse grids along one direction (Site.Smooth).
static void smooth(float *a, int axis, float keep)
{
    float lose = 1.0f - keep;
    for (int round = 0; round < 2; round++)
    {
        if (axis == 0)
        {
            for (int row = 0; row < A3; row += A)
            {
                float r0 = 0.0f;
                for (int c0 = row; c0 < row + A; c0++) { r0 = a[c0] + (r0 - a[c0]) * keep; a[c0] = r0; }
                r0 = 0.0f;
                for (int c0 = row + A - 1; c0 >= row; c0--) { r0 = a[c0] + (r0 - a[c0]) * keep; a[c0] = r0; }
            }
        }
        else
        {
            int step = axis == 1 ? A : A * A;
            for (int k = 0; k < A; k++)
                for (int j = 0; j < A; j++)
                {
                    int row = (j + k * A) * A;
                    if ((axis == 1 ? j : k) == 0) for (int cell = row; cell < row + A; cell++) a[cell] *= lose;
                    else for (int cell = row; cell < row + A; cell++) a[cell] += (a[cell - step] - a[cell]) * keep;
                }
            for (int k = A - 1; k >= 0; k--)
                for (int j = A - 1; j >= 0; j--)
                {
                    int row = (j + k * A) * A;
                    if ((axis == 1 ? j : k) == A - 1) for (int cell = row; cell < row + A; cell++) a[cell] *= lose;
                    else for (int cell = row; cell < row + A; cell++) a[cell] += (a[cell + step] - a[cell]) * keep;
                }
        }
        for (int cell = 0; cell < A3; cell++) if (a[cell] < 1e-9f) a[cell] = 0.0f;
    }
}

static void around_smooth(Work *w, const Grid *g, int which)
{
    float *a = which == 0 ? w->roundSmoke : which == 1 ? w->roundPale : w->roundDust;
    float width = 0.25f * g->repeat, bend = 0.06f * g->repeat;
    for (int axis = 0; axis < 3; axis++)
    {
        float cell = 2.0f * (axis == 0 ? g->dX : axis == 1 ? g->dY : g->dZ);
        float v0 = width * width / (cell * cell) - 0.25f;
        float v = 0.5f * (0.05f > v0 ? 0.05f : v0);
        smooth(a, axis, (v + 1.0f - (float)sqrt((double)(2.0f * v + 1.0f))) / v);
        if (which != 0) continue;
        int spread = 1 + (int)ceil((double)(bend / cell));
        if (spread > 6) spread = 6;
        float *most = w->anything, *line = w->line;
        int stride = axis == 0 ? 1 : axis == 1 ? A : A * A, other1 = axis == 0 ? A : 1, other2 = axis == 2 ? A : A * A;
        for (int j = 0; j < A; j++)
            for (int i = 0; i < A; i++)
            {
                int start = i * other1 + j * other2, since = A;
                for (int k = 0; k < A; k++)
                {
                    float here = most[start + k * stride];
                    since = here > g->something ? 0 : since + 1;
                    line[k] = here > g->something || since <= spread ? 1.0f : 0.0f;
                }
                since = A;
                for (int k = A - 1; k >= 0; k--)
                {
                    int at = start + k * stride;
                    since = most[at] > g->something ? 0 : since + 1;
                    most[at] = line[k] > 0.0f || since <= spread ? 1.0f : 0.0f;
                }
            }
    }
}

static void around(Work *w, const Grid *g)
{
    for (int k = 0; k < A; k++) around_layer(w, g, k);
    for (int which = 0; which < 3; which++) around_smooth(w, g, which);
    const float *most = w->anything, *smokes = w->roundSmoke, *pales = w->roundPale, *dusts = w->roundDust;
    uint8_t *out = g->cellsC;
    for (int c = 0; c < A3; c++)
    {
        float smoke = smokes[c], about = smoke * (0.125f / g->thickest);
        float own = smoke > 1e-7f ? pales[c] / smoke : 0.0f, dust = smoke > 1e-7f ? dusts[c] / smoke : 0.0f;
        int aboutAt = about >= 1.0f ? 65535 : about <= 0.0f ? 0 : to_int(about * 65535.0f), ownAt = own >= 1.0f ? 65535 : own <= 0.0f ? 0 : to_int(own * 65535.0f);
        if (aboutAt < 0) aboutAt = 0;        // (no number at all: the game's own code would have stopped here)
        if (ownAt < 0) ownAt = 0;
        out[c * 4] = rootByte[aboutAt];
        out[c * 4 + 1] = (uint8_t)(most[c] > g->something ? 255 : 0);
        out[c * 4 + 2] = rootByte[ownAt];
        out[c * 4 + 3] = (uint8_t)to_int((dust > 1.0f ? 1.0f : dust < 0.0f ? 0.0f : dust) * 255.0f);
    }
}

// ---------------------------------------------------------------- where the smoke is going and where it "was" (Site.Carry)

// A distance of up to restFar either way, as a number of sixteen bits (Site.Far).
static inline int far_(float metres, float restFar)
{
    float share = metres * (0.5f / restFar) + 0.5f;
    return share <= 0.0f ? 0 : share >= 1.0f ? 65535 : to_int(share * 65535.0f + 0.5f);
}

// A number of half precision from one of single, to the nearest. (Only where the graphics card will not read
// plain sixteen-bit numbers smoothly, see Assets.Sixteen; Unity's own conversion may differ from this in the last place.)
static uint16_t half_(float value)
{
    union { float f; uint32_t u; } b;
    b.f = value;
    uint32_t sign = (b.u >> 16) & 0x8000u, m = b.u & 0x007fffffu;
    int e = (int)((b.u >> 23) & 0xffu) - 127 + 15;
    if (e <= 0)
    {
        if (e < -10) return (uint16_t)sign;
        m = (m | 0x00800000u) >> (1 - e);
        if (m & 0x00001000u) m += 0x00002000u;
        return (uint16_t)(sign | (m >> 13));
    }
    if (e == 0xff - (127 - 15)) return (uint16_t)(sign | 0x7c00u | (m != 0 ? 0x0200u : 0u));
    if (m & 0x00001000u)
    {
        m += 0x00002000u;
        if (m & 0x00800000u) { m = 0; e += 1; }
    }
    if (e > 30) return (uint16_t)(sign | 0x7c00u);
    return (uint16_t)(sign | ((uint32_t)e << 10) | (m >> 13));
}

// Smooth those grids over a cell or so each way (Site.CarrySmooth), all of them at once: six times over, so the
// result is back where it came from.
static void carry_smooth(float *grid, float *temp)
{
    float *restrict a = grid, *restrict b = temp;
    for (int axis = 0; axis < 3; axis++)
        for (int round = 0; round < 2; round++)
        {
            for (int k = 0; k < A; k++)
                for (int j = 0; j < A; j++)
                {
                    int row = (j + k * A) * A;
                    if (axis == 0)
                    {
                        for (int cell = row; cell < row + A; cell++)
                        {
                            // (the cells to either side; at the ends of the row, this cell itself)
                            const float *here = a + (size_t)cell * CARRIED, *low = cell > row ? here - CARRIED : here, *high = cell < row + A - 1 ? here + CARRIED : here;
                            float *out = b + (size_t)cell * CARRIED;
                            for (int f = 0; f < CARRIED; f++) out[f] = 0.5f * here[f] + 0.25f * (low[f] + high[f]);
                        }
                    }
                    else
                    {
                        // (the rows to either side along this direction; at the ends, this row itself)
                        int low = axis == 1 ? (j > 0 ? -A : 0) : (k > 0 ? -A * A : 0), high = axis == 1 ? (j < A - 1 ? A : 0) : (k < A - 1 ? A * A : 0);
                        const float *here = a + (size_t)row * CARRIED, *below = here + low * CARRIED, *above = here + high * CARRIED;
                        float *out = b + (size_t)row * CARRIED;
                        for (int n = 0; n < A * CARRIED; n++) out[n] = 0.5f * here[n] + 0.25f * (below[n] + above[n]);
                    }
                }
            float *swap = a; a = b; b = swap;
        }
}

// The C# keeps each of the twenty-nine figures in a grid of its own. Here they are worked on side by side, all
// twenty-nine for a cell together: every step of this goes through the figures cell by cell, and twenty-nine
// separate grids, which all lie at the same places in their pages of memory, fight each other for the same
// few places in the processor's nearest memory at every cell. (It is the same sums either way; laid out like
// this they take a twentieth of the time.) They are put into the grids the game reads from at the end.
static void carry(Work *w, Grid *g)
{
    float *restrict c = w->carried;
    memset(c, 0, (size_t)A3 * CARRIED * sizeof(float));
    const P *a = g->particles;
    int n = g->count;
    float x0 = g->x0, y0 = g->y0, z0 = g->z0, hx = 2.0f * g->dX, hy = 2.0f * g->dY, hz = 2.0f * g->dZ, least = g->leastReach, hold = g->hold;
    float ix = 1.0f / hx, iy = 1.0f / hy, iz = 1.0f / hz;
    float goesX = g->goesX, goesY = g->goesY, goesZ = g->goesZ;
    const float *bells = g->bells;
    for (int i = 0; i < n; i++)
    {
        float r = a[i].r, life = a[i].life, age = a[i].age;
        if (r <= 0.01f || life < 0.0f) continue;
        float fade = life - age < life * 0.22f ? (life - age) / (life * 0.22f) : 1.0f;
        if (a[i].fadeIn > 0.0f && age < a[i].fadeIn) fade *= age / a[i].fadeIn;
        float shows = (a[i].mass / (r * r * r) + a[i].flame * a[i].heat / r) * fade;
        if (shows < 1e-6f) continue;
        float px = a[i].x, py = a[i].y, pz = a[i].z;
        float dies = a[i].kloss * hold * 0.4f * g->ahead, left = 1.0f / (1.0f + dies);
        float f[CARRIED];
        f[0] = 1.0f;
        f[1] = a[i].vx + a[i].kx * left - goesX; f[2] = a[i].vy + a[i].ky * left - goesY; f[3] = a[i].vz + a[i].kz * left - goesZ;
        // How much each of its six reckonings counts for just now, by where it is in its round; where it "was" by a
        // reckoning goes in for as much as that reckoning counts with it (see the C#).
        float round = a[i].turn, rate = a[i].rate;
        if (!(round >= 0.0f && round < 1.0f)) round = 0.0f;
        if (!(rate >= 0.0f && rate < 8.0f)) rate = 0.0f;
        float b0 = bell_(bells, round), b1 = bell_(bells, round + THIRD), b2 = bell_(bells, round + 2.0f * THIRD);
        float b3 = bell_(bells, 2.0f * round), b4 = bell_(bells, 2.0f * round + THIRD), b5 = bell_(bells, 2.0f * round + 2.0f * THIRD);
        // (the reckonings in order: the big billows' three, a, b and e, then the small ones' three, c, d and f)
        f[4] = (a[i].ax - px) * b0; f[5] = (a[i].ay - py) * b0; f[6] = (a[i].az - pz) * b0; f[7] = (a[i].bx - px) * b1; f[8] = (a[i].by - py) * b1; f[9] = (a[i].bz - pz) * b1;
        f[10] = (a[i].ex - px) * b2; f[11] = (a[i].ey - py) * b2; f[12] = (a[i].ez - pz) * b2;
        f[13] = (a[i].cx - px) * b3; f[14] = (a[i].cy - py) * b3; f[15] = (a[i].cz - pz) * b3; f[16] = (a[i].dx - px) * b4; f[17] = (a[i].dy - py) * b4; f[18] = (a[i].dz - pz) * b4;
        f[19] = (a[i].fx - px) * b5; f[20] = (a[i].fy - py) * b5; f[21] = (a[i].fz - pz) * b5;
        f[COUNTS] = b0; f[COUNTS + 1] = b1; f[COUNTS + 2] = b2; f[COUNTS + 3] = b3; f[COUNTS + 4] = b4; f[COUNTS + 5] = b5;
        f[RATE] = rate;
        float reach = 1.474f * r;
        if (reach < least) reach = least;
        float rx = reach < 1.2f * hx ? 1.2f * hx : reach > 2.5f * hx ? 2.5f * hx : reach, ry = reach < 1.2f * hy ? 1.2f * hy : reach > 2.5f * hy ? 2.5f * hy : reach, rz = reach < 1.2f * hz ? 1.2f * hz : reach > 2.5f * hz ? 2.5f * hz : reach;
        shows *= 0.95744f * rx * ry * rz * ix * iy * iz;
        float u = (px - x0) * ix - 0.5f, v = (py - y0) * iy - 0.5f, w3 = (pz - z0) * iz - 0.5f;
        if (!(u > -1.0f && v > -1.0f && w3 > -1.0f && u < A && v < A && w3 < A)) continue;      // (and nothing that is no number at all)
        int ci = (int)u, cj = (int)v, ck = (int)w3;
        if (u < ci) ci--; if (v < cj) cj--; if (w3 < ck) ck--;
        float fu = u - ci, fv = v - cj, fw = w3 - ck;
        for (int corner = 0; corner < 8; corner++)
        {
            int di = corner & 1, dj = (corner >> 1) & 1, dk = corner >> 2, ii = ci + di, jj = cj + dj, kk = ck + dk;
            if (ii < 0 || jj < 0 || kk < 0 || ii >= A || jj >= A || kk >= A) continue;
            float wt = (di == 0 ? 1.0f - fu : fu) * (dj == 0 ? 1.0f - fv : fv) * (dk == 0 ? 1.0f - fw : fw) * shows;
            if (wt <= 0.0f) continue;
            float *cell = c + (size_t)(ii + (jj + kk * A) * A) * CARRIED;
            // (the first of them is how much there is: the weight itself)
            for (int k = 0; k < CARRIED; k++) cell[k] += wt * f[k];
        }
    }
    carry_smooth(c, w->carriedTemp);
    // What is left is sums; each cell's share of them is the smoke's own figure there.
    uint8_t *known = w->known;
    for (int cell = 0; cell < A3; cell++)
    {
        float *here = c + (size_t)cell * CARRIED;
        if (here[0] > 1e-30f)
        {
            float per = 1.0f / here[0];
            for (int k = 1; k < 4; k++) here[k] *= per;
            for (int which = 0; which < RECKONINGS; which++)
            {
                float counts = here[COUNTS + which], each = counts > 1e-6f * here[0] ? 1.0f / counts : 0.0f;
                here[4 + 3 * which] *= each; here[5 + 3 * which] *= each; here[6 + 3 * which] *= each;
                here[COUNTS + which] = counts * per;
            }
            here[RATE] *= per;
            known[cell] = 1;
        }
        else
        {
            for (int k = 1; k < CARRIED; k++) here[k] = 0.0f;
            known[cell] = 0;
        }
    }
    // Outward from there, a cell at a time: each empty cell takes the mean of its neighbours that have something.
    float *sums = w->sums;
    for (int round = 0; round < 5; round++)
    {
        uint8_t mark = (uint8_t)(round + 2);
        for (int k = 0; k < A; k++)
            for (int j = 0; j < A; j++)
                for (int i = 0; i < A; i++)
                {
                    int cell = i + (j + k * A) * A;
                    if (known[cell] != 0) continue;
                    int near = 0;
                    for (int side = 0; side < 6; side++)
                    {
                        int other;
                        if (side == 0) { if (i == 0) continue; other = cell - 1; }
                        else if (side == 1) { if (i == A - 1) continue; other = cell + 1; }
                        else if (side == 2) { if (j == 0) continue; other = cell - A; }
                        else if (side == 3) { if (j == A - 1) continue; other = cell + A; }
                        else if (side == 4) { if (k == 0) continue; other = cell - A * A; }
                        else { if (k == A - 1) continue; other = cell + A * A; }
                        if (known[other] == 0 || known[other] == mark) continue;
                        const float *there = c + (size_t)other * CARRIED;
                        if (near++ == 0) for (int f = 1; f < CARRIED; f++) sums[f] = there[f];
                        else for (int f = 1; f < CARRIED; f++) sums[f] += there[f];
                    }
                    if (near == 0) continue;
                    float share = 1.0f / near;
                    float *here = c + (size_t)cell * CARRIED;
                    for (int f = 1; f < CARRIED; f++) here[f] = sums[f] * share;
                    known[cell] = mark;
                }
    }
    float fastest = 2.0f;
    for (int cell = 0; cell < A3; cell++)
    {
        const float *here = c + (size_t)cell * CARRIED;
        float ux = here[1], uy = here[2], uz = here[3];
        if (ux < 0.0f) ux = -ux; if (uy < 0.0f) uy = -uy; if (uz < 0.0f) uz = -uz;
        if (ux > fastest) fastest = ux; if (uy > fastest) fastest = uy; if (uz > fastest) fastest = uz;
    }
    g->fastest = fastest;
    float scale = 127.5f / fastest;
    float *strain = g->strain;
    uint8_t *out = g->cellsD, *outE = g->cellsE;
    for (int k = 0; k < A; k++)
        for (int j = 0; j < A; j++)
            for (int i = 0; i < A; i++)
            {
                int cell = i + (j + k * A) * A;
                // (how much the pattern of billows is squeezed here: see the C#)
                if (known[cell] == 0)
                {
                    out[cell * 4] = 127; out[cell * 4 + 1] = 127; out[cell * 4 + 2] = 127; out[cell * 4 + 3] = 0;
                    outE[cell * 4] = 128; outE[cell * 4 + 1] = 128; outE[cell * 4 + 2] = 128; outE[cell * 4 + 3] = 128;
                    strain[cell] = 0.0f;
                    continue;
                }
                int before = i > 0 ? cell - 1 : cell, after = i < A - 1 ? cell + 1 : cell, below = j > 0 ? cell - A : cell, above = j < A - 1 ? cell + A : cell, south = k > 0 ? cell - A * A : cell, north = k < A - 1 ? cell + A * A : cell;
                float perX = 1.0f / ((after - before) * hx), perY = A / ((above - below) * hy), perZ = (float)(A * A) / ((north - south) * hz), most = 1.0f;
                const float *cBefore = c + (size_t)before * CARRIED, *cAfter = c + (size_t)after * CARRIED, *cBelow = c + (size_t)below * CARRIED, *cAbove = c + (size_t)above * CARRIED, *cSouth = c + (size_t)south * CARRIED, *cNorth = c + (size_t)north * CARRIED;
                for (int f = 4; f < COUNTS; f += 3)
                {
                    if (after != before)
                    {
                        float gx = (cAfter[f] - cBefore[f]) * perX + 1.0f, gy = (cAfter[f + 1] - cBefore[f + 1]) * perX, gz = (cAfter[f + 2] - cBefore[f + 2]) * perX, gg = gx * gx + gy * gy + gz * gz;
                        if (gg > most) most = gg;
                    }
                    if (above != below)
                    {
                        float gx = (cAbove[f] - cBelow[f]) * perY, gy = (cAbove[f + 1] - cBelow[f + 1]) * perY + 1.0f, gz = (cAbove[f + 2] - cBelow[f + 2]) * perY, gg = gx * gx + gy * gy + gz * gz;
                        if (gg > most) most = gg;
                    }
                    if (north != south)
                    {
                        float gx = (cNorth[f] - cSouth[f]) * perZ, gy = (cNorth[f + 1] - cSouth[f + 1]) * perZ, gz = (cNorth[f + 2] - cSouth[f + 2]) * perZ + 1.0f, gg = gx * gx + gy * gy + gz * gz;
                        if (gg > most) most = gg;
                    }
                }
                // (how fast the air here is pulling the smoke out of shape, and how much each reckoning counts here: see the C#)
                const float *mine = c + (size_t)cell * CARRIED;
                float xx = 0.0f, xy = 0.0f, xz = 0.0f, yx = 0.0f, yy = 0.0f, yz = 0.0f, zx = 0.0f, zy = 0.0f, zz = 0.0f;
                if (after != before) { xx = (cAfter[1] - cBefore[1]) * perX; yx = (cAfter[2] - cBefore[2]) * perX; zx = (cAfter[3] - cBefore[3]) * perX; }
                if (above != below) { xy = (cAbove[1] - cBelow[1]) * perY; yy = (cAbove[2] - cBelow[2]) * perY; zy = (cAbove[3] - cBelow[3]) * perY; }
                if (north != south) { xz = (cNorth[1] - cSouth[1]) * perZ; yz = (cNorth[2] - cSouth[2]) * perZ; zz = (cNorth[3] - cSouth[3]) * perZ; }
                float sxy = xy + yx, sxz = xz + zx, syz = yz + zy;
                float pull = root_(xx * xx + yy * yy + zz * zz + 0.5f * (sxy * sxy + sxz * sxz + syz * syz));
                strain[cell] = pull < 1e6f ? pull : 0.0f;                               // (and nothing that is no number at all)
                // (whose turn it is here, as the point on a circle that the three reckonings' shares are the heights of: see the C#)
                float s0 = mine[COUNTS], s1 = mine[COUNTS + 1], s2 = mine[COUNTS + 2], s3 = mine[COUNTS + 3], s4 = mine[COUNTS + 4], s5 = mine[COUNTS + 5];
                float re = (0.5f * (s1 + s2) - s0) * 170.0f + 128.0f, im = (s1 - s2) * 147.224f + 128.0f, re2 = (0.5f * (s4 + s5) - s3) * 170.0f + 128.0f, im2 = (s4 - s5) * 147.224f + 128.0f;
                outE[cell * 4] = (uint8_t)to_int(re < 0.0f ? 0.0f : re > 255.0f ? 255.0f : re); outE[cell * 4 + 1] = (uint8_t)to_int(im < 0.0f ? 0.0f : im > 255.0f ? 255.0f : im);
                outE[cell * 4 + 2] = (uint8_t)to_int(re2 < 0.0f ? 0.0f : re2 > 255.0f ? 255.0f : re2); outE[cell * 4 + 3] = (uint8_t)to_int(im2 < 0.0f ? 0.0f : im2 > 255.0f ? 255.0f : im2);
                float squeezed = most > 1.0001f ? 0.72135f * (float)log((double)most) * 63.75f : 0.0f;
                const float *here = c + (size_t)cell * CARRIED;
                out[cell * 4] = (uint8_t)to_int(127.5f + here[1] * scale + 0.5f);
                out[cell * 4 + 1] = (uint8_t)to_int(127.5f + here[2] * scale + 0.5f);
                out[cell * 4 + 2] = (uint8_t)to_int(127.5f + here[3] * scale + 0.5f);
                out[cell * 4 + 3] = (uint8_t)to_int(squeezed > 255.0f ? 255.0f : squeezed);
            }
    // Eighteen numbers a cell, sixteen bits each, four to a texture (the last holds two).
    uint16_t *rest0 = g->cellsRest[0], *rest1 = g->cellsRest[1], *rest2 = g->cellsRest[2], *rest3 = g->cellsRest[3], *rest4 = g->cellsRest[4];
    float restFar = g->restFar;
    if (g->halves)
        for (int cell = 0; cell < A3; cell++)
        {
            const float *here = c + (size_t)cell * CARRIED + 4;
            for (int n = 0; n < 4; n++) { rest0[cell * 4 + n] = half_(here[n]); rest1[cell * 4 + n] = half_(here[4 + n]); rest2[cell * 4 + n] = half_(here[8 + n]); rest3[cell * 4 + n] = half_(here[12 + n]); }
            rest4[cell * 4] = half_(here[16]); rest4[cell * 4 + 1] = half_(here[17]); rest4[cell * 4 + 2] = half_(here[RATE - 4]);
        }
    else
        for (int cell = 0; cell < A3; cell++)
        {
            const float *here = c + (size_t)cell * CARRIED + 4;
            for (int n = 0; n < 4; n++)
            {
                rest0[cell * 4 + n] = (uint16_t)far_(here[n], restFar); rest1[cell * 4 + n] = (uint16_t)far_(here[4 + n], restFar);
                rest2[cell * 4 + n] = (uint16_t)far_(here[8 + n], restFar); rest3[cell * 4 + n] = (uint16_t)far_(here[12 + n], restFar);
            }
            rest4[cell * 4] = (uint16_t)far_(here[16], restFar); rest4[cell * 4 + 1] = (uint16_t)far_(here[17], restFar); rest4[cell * 4 + 2] = (uint16_t)far_(here[RATE - 4], restFar);
        }
    // And into the grids the game itself reads from (see Site.RestAt), a row of cells at a time.
    float **grids = g->carry;
    for (int row = 0; row < A3; row += A)
        for (int k = 0; k < CARRIED; k++)
        {
            float *into = grids[k] + row;
            const float *from = c + (size_t)row * CARRIED + k;
            for (int cell = 0; cell < A; cell++) into[cell] = from[(size_t)cell * CARRIED];
        }
}

// ---------------------------------------------------------------- packed for the shader (Site.Compose, Site.Clearance)

static void compose(Work *w, const Grid *g)
{
    const float *smokeIn = w->field[0], *flameIn = w->field[3], *hotIn = w->field[4], *sunNext = g->sunThrough, *skyNext = g->skyThrough;
    uint8_t *outA = g->cellsA, *outB = g->cellsB;
    float thickest = g->thickest, thickestFlame = g->thickestFlame, mostBefore = g->mostBefore, mostAbove = g->mostAbove;
    int sun = g->sunUp;
    for (int at = 0; at < G3; at++)
    {
        float smoke = smokeIn[at], flame = flameIn[at];
        int deep = smoke >= thickest ? 65535 : to_int(smoke / thickest * 65535.0f + 0.5f), burning = flame >= thickestFlame ? 65535 : to_int(flame / thickestFlame * 65535.0f + 0.5f);
        float before = sun ? sunNext[at] : mostBefore, above = skyNext[at];
        int shaded = before >= mostBefore ? 65535 : to_int(before / mostBefore * 65535.0f + 0.5f);
        float heat = flame > 1e-4f ? hotIn[at] / flame : 0.0f;
        outA[at * 4] = (uint8_t)(shaded >> 8); outA[at * 4 + 1] = (uint8_t)(shaded & 255);
        outA[at * 4 + 2] = (uint8_t)to_int(above >= mostAbove ? 255.0f : above / mostAbove * 255.0f + 0.5f);
        outA[at * 4 + 3] = (uint8_t)to_int((heat > 1.2f ? 1.0f : heat / 1.2f) * 255.0f);
        outB[at * 4] = (uint8_t)(deep >> 8); outB[at * 4 + 1] = (uint8_t)(deep & 255); outB[at * 4 + 2] = (uint8_t)(burning >> 8); outB[at * 4 + 3] = (uint8_t)(burning & 255);
    }
}

#define CLEAR_FAR 40
#define W (A + 2)
#define W2 (W * W)

static void clearance(Work *w, const Grid *g)
{
    uint8_t *d = w->clear;
    const uint8_t *packed = g->cellsB;
    memset(d, CLEAR_FAR, (size_t)W * W * W);
    for (int k = 0; k < A; k++)
        for (int j = 0; j < A; j++)
        {
            int at = (k + 1) * W2 + (j + 1) * W + 1, under = (2 * j + 2 * k * G) * G;
            for (int i = 0; i < A; i++)
            {
                int cell = under + 2 * i;
                uint32_t any;
                uint32_t part;
                memcpy(&any, packed + (size_t)cell * 4, 4);
                memcpy(&part, packed + (size_t)(cell + 1) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G + 1) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G2) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G2 + 1) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G2 + G) * 4, 4); any |= part;
                memcpy(&part, packed + (size_t)(cell + G2 + G + 1) * 4, 4); any |= part;
                d[at + i] = any != 0 ? 0 : CLEAR_FAR;
            }
        }
    for (int k = 1; k <= A; k++)
        for (int j = 1; j <= A; j++)
        {
            int at = 1 + j * W + k * W2;
            for (int i = 1; i <= A; i++, at++)
            {
                int v = d[at];
                if (v == 0) continue;
                int m = d[at - 1];
                int o = at - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                o = at - W2 - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                if (m + 1 < v) d[at] = (uint8_t)(m + 1);
            }
        }
    uint8_t *into = g->cellsClear;
    for (int k = A; k >= 1; k--)
        for (int j = A; j >= 1; j--)
        {
            int at = A + j * W + k * W2, cell = A - 1 + ((j - 1) + (k - 1) * A) * A;
            for (int i = A; i >= 1; i--, at--, cell--)
            {
                int v = d[at];
                if (v != 0)
                {
                    int m = d[at + 1];
                    int o = at + W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                    o = at + W2 - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                    o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                    o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                    if (m + 1 < v) { v = m + 1; d[at] = (uint8_t)v; }
                }
                into[cell] = (uint8_t)v;
            }
        }
}

// ---------------------------------------------------------------- the whole of it (the second half of Site.Lighting)

// Nought if the grid was made; otherwise which of the things it was handed did not fit what this was built for.
EXPORT int vfx_grid(void *work, Grid *g)
{
    Work *w = (Work *)work;
    if (w == NULL || g == NULL) return 1;
    if (g->size != (int32_t)sizeof(Grid) || g->g != G || g->a != A || g->levels != LEVELS || g->carried != CARRIED) return 2;
    if (g->particles == NULL || g->count < 0 || g->sunThrough == NULL || g->skyThrough == NULL) return 3;
    if (g->drawn && (g->carry == NULL || g->cellsA == NULL || g->cellsB == NULL || g->cellsC == NULL || g->cellsD == NULL || g->cellsRest == NULL || g->cellsClear == NULL)) return 4;
    if (g->floored && g->floors == NULL) return 5;
    if (g->drawn && (g->cellsE == NULL || g->strain == NULL || g->bells == NULL)) return 6;
    for (int n = 0; n < 8; n++) g->nanoseconds[n] = 0;
    int64_t t0 = now_ns();
    for (int c = 0; c < 5; c++) memset(w->field[c], 0, G3 * sizeof(float));
    for (int level = 1; level < LEVELS; level++)
    {
        int side = G >> level;
        w->wideUsed[level] = 0;
        for (int c = 0; c < 5; c++) memset(w->wide[level][c], 0, (size_t)side * side * side * sizeof(float));
    }
    deposit(w, g);
    if (w->wideUsed[1] || w->wideUsed[2] || w->wideUsed[3]) for (int which = 0; which < 5; which++) gather(w, which);
    int64_t t1 = now_ns();
    g->nanoseconds[1] = t1 - t0;
    // (less than the smoke really blocks: light also finds its way round inside a cloud)
    if (g->sunUp) follow(w, g, g->sunX, g->sunY, g->sunZ, g->sunThrough, 0.45f);
    int64_t t2 = now_ns();
    g->nanoseconds[2] = t2 - t1;
    follow(w, g, g->skyX, g->skyY, g->skyZ, g->skyThrough, 0.55f);
    int64_t t3 = now_ns();
    g->nanoseconds[3] = t3 - t2;
    // (what follows is all for the shader: where the smoke is drawn as sprites none of it is asked for)
    if (g->drawn)
    {
        around(w, g);
        int64_t t4 = now_ns();
        g->nanoseconds[4] = t4 - t3;
        carry(w, g);
        int64_t t5 = now_ns();
        g->nanoseconds[5] = t5 - t4;
        compose(w, g);
        clearance(w, g);
        g->nanoseconds[7] = now_ns() - t5;
    }
    return 0;
}
