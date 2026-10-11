// Run the Windows (PE) and Linux (ELF) builds of the explosions' library on a Mac, in an x86_64 process (Rosetta on
// Apple's own processors), and compare what they make with the Mac's build of the same source, byte for byte.
//
//   python3 tools/native/genp.py src/VolumetricExplosions/Site.cs <folder>/p.h     (and the Mac library built for x86_64, as build.sh does)
//   cc -arch x86_64 -O1 -I <folder> -o foreign_check tools/native/foreign_check.c
//   ./foreign_check <Mac x86_64 vfxgrid.dylib> vfxgrid.dll vfxgrid.so
//
// The DLL is mapped by hand: its sections, its base relocations, and its imports bound to stand-ins written here
// with Windows' calling convention (calloc, free and the clock are real; anything else stops this loudly, so it also
// shows that the grid code needs nothing more). The .so likewise: its segments and relocations, its imports bound
// to this Mac's own C library, which has Linux's calling convention. Each is then called as the mod calls it.
//
// What this checks: the files' own code (their exports, calling convention, the layouts of a particle and of a
// grid, and every sum, on made-up fires). What it does not: Windows' and Linux's loaders, their C runtimes (the
// DLL's own start-up code is not run: it reads Windows' thread block through the gs register, which a Mac does not
// have), and the game's Mono calling in.
#include <dlfcn.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include "p.h"

#define G 64
#define G2 (G * G)
#define G3 (G * G * G)
#define A 32
#define A3 (A * A * A)
#define LEVELS 4
#define CARRIED 29
#define BELL_STEPS 1024
#define MSABI __attribute__((ms_abi))

typedef struct Grid
{
    int32_t size, g, a, levels, carried;
    int32_t count, drawn, sunUp, halves, floored;
    const P *particles;
    float x0, y0, z0, dX, dY, dZ;
    float keepX0, keepX1, keepY0, keepY1, keepZ0, keepZ1, fadeX, fadeY, fadeZ;
    float leastReach, thick, hold, goesX, goesY, goesZ, ahead;
    float sunX, sunY, sunZ, repeat;
    float skyX, skyY, skyZ;
    float thickest, thickestFlame, something, restFar, mostBefore, mostAbove;
    float fastest;
    float *sunThrough, *skyThrough;
    float **carry;
    uint8_t *cellsA, *cellsB, *cellsC, *cellsD;
    uint16_t **cellsRest;
    uint8_t *cellsClear;
    const float *floors;
    uint8_t *cellsE;
    float *strain;
    const float *bells;
    int64_t nanoseconds[8];
} Grid;

// ------------------------------------------------------------------ one implementation: six functions, called one way or the other

typedef struct Lib
{
    const char *what;
    int ms;                                              // Windows' calling convention
    void *f[6];                                          // signature, size_of_p, size_of_grid, new, free, grid
} Lib;

static const char *Names[6] = { "vfx_signature", "vfx_size_of_p", "vfx_size_of_grid", "vfx_new", "vfx_free", "vfx_grid" };

static int call_int(Lib *l, int k) { return l->ms ? ((int (MSABI *)(void))l->f[k])() : ((int (*)(void))l->f[k])(); }
static void *call_new(Lib *l) { return l->ms ? ((void *(MSABI *)(void))l->f[3])() : ((void *(*)(void))l->f[3])(); }
static void call_free(Lib *l, void *w) { if (l->ms) ((void (MSABI *)(void *))l->f[4])(w); else ((void (*)(void *))l->f[4])(w); }
static int call_grid(Lib *l, void *w, Grid *g) { return l->ms ? ((int (MSABI *)(void *, Grid *))l->f[5])(w, g) : ((int (*)(void *, Grid *))l->f[5])(w, g); }

static uint8_t *read_file(const char *path, size_t *size)
{
    FILE *f = fopen(path, "rb");
    if (!f) { perror(path); exit(1); }
    fseek(f, 0, SEEK_END); *size = (size_t)ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *b = malloc(*size);
    if (fread(b, 1, *size, f) != *size) { perror(path); exit(1); }
    fclose(f);
    return b;
}

static void *room(size_t size)
{
    void *at = mmap(NULL, size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON, -1, 0);
    if (at == MAP_FAILED) { perror("mmap"); exit(1); }
    return at;
}

// ------------------------------------------------------------------ Windows: stand-ins for what the DLL imports

static MSABI void *w_calloc(size_t n, size_t s) { return calloc(n, s); }
static MSABI void w_free(void *p) { free(p); }
static MSABI int w_QueryPerformanceCounter(int64_t *c) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); *c = (int64_t)t.tv_sec * 1000000000 + t.tv_nsec; return 1; }
static MSABI int w_QueryPerformanceFrequency(int64_t *f) { *f = 1000000000; return 1; }
// (everything else it imports is for the C runtime's own start-up and shutting down, which is not run here)
static MSABI void w_never(void) { fprintf(stderr, "the DLL called something other than calloc, free or the clock: not expected of the grid code\n"); abort(); }

static const struct { const char *name; void *fn; } WinStubs[] = {
    { "calloc", (void *)w_calloc }, { "free", (void *)w_free },
    { "QueryPerformanceCounter", (void *)w_QueryPerformanceCounter }, { "QueryPerformanceFrequency", (void *)w_QueryPerformanceFrequency },
};

#define U16(p) (*(const uint16_t *)(p))
#define U32(p) (*(const uint32_t *)(p))
#define U64(p) (*(const uint64_t *)(p))

static void load_pe(const char *path, Lib *l)
{
    size_t size;
    uint8_t *file = read_file(path, &size);
    if (file[0] != 'M' || file[1] != 'Z') { fprintf(stderr, "%s: not a PE file\n", path); exit(1); }
    const uint8_t *pe = file + U32(file + 0x3c);
    if (U32(pe) != 0x4550 || U16(pe + 4) != 0x8664) { fprintf(stderr, "%s: not an x86-64 PE file\n", path); exit(1); }
    int sections = U16(pe + 6), optional = U16(pe + 20);
    const uint8_t *opt = pe + 24;
    if (U16(opt) != 0x20b) { fprintf(stderr, "%s: not PE32+\n", path); exit(1); }
    uint64_t image_base = U64(opt + 24);
    uint32_t image_size = U32(opt + 56), headers = U32(opt + 60);
    const uint8_t *dirs = opt + 112;
    uint8_t *base = room(image_size);
    memcpy(base, file, headers);
    const uint8_t *sec = opt + optional;
    for (int s = 0; s < sections; s++, sec += 40)
    {
        uint32_t vsize = U32(sec + 8), va = U32(sec + 12), rawsize = U32(sec + 16), raw = U32(sec + 20);
        memcpy(base + va, file + raw, rawsize < vsize || vsize == 0 ? rawsize : vsize);
    }
    // base relocations
    uint32_t rel = U32(dirs + 5 * 8), rel_size = U32(dirs + 5 * 8 + 4);
    int64_t delta = (int64_t)(uintptr_t)base - (int64_t)image_base;
    int relocated = 0;
    for (uint32_t at = 0; at < rel_size;)
    {
        uint32_t page = U32(base + rel + at), block = U32(base + rel + at + 4);
        for (uint32_t k = 8; k < block; k += 2)
        {
            uint16_t e = U16(base + rel + at + k);
            int type = e >> 12, off = e & 0xfff;
            if (type == 0) continue;
            if (type != 10) { fprintf(stderr, "%s: base relocation of kind %d\n", path, type); exit(1); }
            *(uint64_t *)(base + page + off) += (uint64_t)delta;
            relocated++;
        }
        at += block;
    }
    // imports
    uint32_t imp = U32(dirs + 1 * 8);
    int bound = 0, real = 0;
    for (const uint8_t *d = base + imp; U32(d + 12) != 0; d += 20)
    {
        const char *dll = (const char *)base + U32(d + 12);
        uint64_t *lookup = (uint64_t *)(base + (U32(d) ? U32(d) : U32(d + 16))), *iat = (uint64_t *)(base + U32(d + 16));
        for (; *lookup; lookup++, iat++)
        {
            if (*lookup >> 63) { fprintf(stderr, "%s: import by number from %s\n", path, dll); exit(1); }
            const char *name = (const char *)base + (uint32_t)*lookup + 2;
            void *fn = (void *)w_never;
            for (size_t n = 0; n < sizeof WinStubs / sizeof WinStubs[0]; n++) if (!strcmp(WinStubs[n].name, name)) { fn = WinStubs[n].fn; real++; }
            *iat = (uint64_t)(uintptr_t)fn;
            bound++;
        }
    }
    // TLS: an index for it (nothing here uses it)
    uint32_t tls = U32(dirs + 9 * 8);
    if (tls) *(uint32_t *)(uintptr_t)U64(base + tls + 16) = 0;
    // exports
    uint32_t exp = U32(dirs);
    const uint8_t *e = base + exp;
    uint32_t nnames = U32(e + 24);
    const uint32_t *fns = (const uint32_t *)(base + U32(e + 28)), *names = (const uint32_t *)(base + U32(e + 32));
    const uint16_t *ords = (const uint16_t *)(base + U32(e + 36));
    for (int k = 0; k < 6; k++)
    {
        l->f[k] = NULL;
        for (uint32_t n = 0; n < nnames; n++)
            if (!strcmp((const char *)base + names[n], Names[k])) l->f[k] = base + fns[ords[n]];
        if (!l->f[k]) { fprintf(stderr, "%s: %s is not exported\n", path, Names[k]); exit(1); }
    }
    l->ms = 1;
    printf("%s: mapped at %p (built for %#llx), %d relocations, %d imports (%d of them real here), %u exports\n", path, (void *)base, (unsigned long long)image_base, relocated, bound, real, nnames);
}

// ------------------------------------------------------------------ Linux: an ELF shared object, bound to this Mac's own C library

static int l_clock_gettime(int clock, struct timespec *t) { (void)clock; return clock_gettime(CLOCK_MONOTONIC, t); }   // (Linux's CLOCK_MONOTONIC is 1, the Mac's 6)

static void *elf_import(const char *name)
{
    if (!strcmp(name, "clock_gettime")) return (void *)l_clock_gettime;
    if (!strcmp(name, "calloc")) return (void *)calloc;
    if (!strcmp(name, "free")) return (void *)free;
    if (!strcmp(name, "memset")) return (void *)memset;
    if (!strcmp(name, "memcpy")) return (void *)memcpy;
    if (!strcmp(name, "floorf")) return (void *)floorf;
    if (!strcmp(name, "ceilf")) return (void *)ceilf;
    if (!strcmp(name, "ceil")) return (void *)ceil;
    if (!strcmp(name, "log")) return (void *)log;
    if (!strcmp(name, "sqrt")) return (void *)sqrt;
    return NULL;
}

static void load_elf(const char *path, Lib *l)
{
    size_t size;
    uint8_t *file = read_file(path, &size);
    if (memcmp(file, "\177ELF", 4) || file[4] != 2 || U16(file + 18) != 62) { fprintf(stderr, "%s: not an x86-64 ELF file\n", path); exit(1); }
    uint64_t phoff = U64(file + 32);
    int phnum = U16(file + 56), phent = U16(file + 54);
    uint64_t top = 0, dyn = 0;
    for (int k = 0; k < phnum; k++)
    {
        const uint8_t *ph = file + phoff + (size_t)k * phent;
        if (U32(ph) == 1 && U64(ph + 16) + U64(ph + 40) > top) top = U64(ph + 16) + U64(ph + 40);
        if (U32(ph) == 2) dyn = U64(ph + 16);
    }
    uint8_t *base = room((top + 0xfff) & ~(uint64_t)0xfff);
    for (int k = 0; k < phnum; k++)
    {
        const uint8_t *ph = file + phoff + (size_t)k * phent;
        if (U32(ph) == 1) memcpy(base + U64(ph + 16), file + U64(ph + 8), U64(ph + 32));
    }
    uint64_t symtab = 0, strtab = 0, rela = 0, relasz = 0, jmprel = 0, pltrelsz = 0, hash = 0;
    for (const uint8_t *d = base + dyn; U64(d) != 0; d += 16)
    {
        uint64_t tag = U64(d), v = U64(d + 8);
        if (tag == 6) symtab = v; else if (tag == 5) strtab = v; else if (tag == 7) rela = v; else if (tag == 8) relasz = v;
        else if (tag == 23) jmprel = v; else if (tag == 2) pltrelsz = v; else if (tag == 4) hash = v;
        else if (tag == 12 || tag == 25) { fprintf(stderr, "%s: has start-up code (DT_INIT/INIT_ARRAY), which this does not run\n", path); }
    }
    int relocated = 0, bound = 0;
    for (int pass = 0; pass < 2; pass++)
    {
        uint64_t from = pass ? jmprel : rela, length = pass ? pltrelsz : relasz;
        for (uint64_t at = 0; at < length; at += 24)
        {
            const uint8_t *r = base + from + at;
            uint64_t off = U64(r), info = U64(r + 8);
            int64_t add = (int64_t)U64(r + 16);
            uint32_t type = (uint32_t)info, sym = (uint32_t)(info >> 32);
            const uint8_t *s = base + symtab + (size_t)sym * 24;
            const char *name = (const char *)base + strtab + U32(s);
            if (type == 8) { *(uint64_t *)(base + off) = (uint64_t)(uintptr_t)base + (uint64_t)add; relocated++; }
            else if (type == 6 || type == 7 || type == 1)
            {
                void *fn = U16(s + 6) != 0 ? (void *)(base + U64(s + 8)) : elf_import(name);
                if (!fn) { fprintf(stderr, "%s: nothing for the import %s\n", path, name); exit(1); }
                *(uint64_t *)(base + off) = (uint64_t)(uintptr_t)fn + (type == 1 ? (uint64_t)add : 0);
                bound++;
            }
            else { fprintf(stderr, "%s: relocation of kind %u\n", path, type); exit(1); }
        }
    }
    uint32_t nsyms = hash ? U32(base + hash + 4) : 0;
    for (int k = 0; k < 6; k++)
    {
        l->f[k] = NULL;
        for (uint32_t n = 0; n < nsyms; n++)
        {
            const uint8_t *s = base + symtab + (size_t)n * 24;
            if (U16(s + 6) != 0 && !strcmp((const char *)base + strtab + U32(s), Names[k])) l->f[k] = base + U64(s + 8);
        }
        if (!l->f[k]) { fprintf(stderr, "%s: %s is not exported\n", path, Names[k]); exit(1); }
    }
    l->ms = 0;
    printf("%s: mapped at %p, %d relative relocations, %d symbols bound, %u symbols\n", path, (void *)base, relocated, bound, nsyms);
}

static void load_dylib(const char *path, Lib *l)
{
    void *h = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!h) { fprintf(stderr, "%s: %s\n", path, dlerror()); exit(1); }
    for (int k = 0; k < 6; k++) if (!(l->f[k] = dlsym(h, Names[k]))) { fprintf(stderr, "%s: no %s\n", path, Names[k]); exit(1); }
    l->ms = 0;
    printf("%s: loaded by the Mac's own loader\n", path);
}

// ------------------------------------------------------------------ the same made-up fire for each

static uint32_t seed;
static float rnd(void) { seed = seed * 1664525u + 1013904223u; return (float)(seed >> 8) / 16777216.0f; }
static float between(float a, float b) { return a + (b - a) * rnd(); }

typedef struct Out
{
    float *sun, *sky, *carry[CARRIED], *strain;
    uint8_t *a, *b, *c, *d, *e, *clear;
    uint16_t *rest[5];
    float fastest;
    int result;
} Out;

static P *particles;
static float floors[G2 + G2 / 4 + G2 / 16 + G2 / 64], bells[BELL_STEPS];

static void make_fire(int count, uint32_t s)
{
    seed = s;
    free(particles);
    particles = calloc((size_t)count, sizeof(P));
    for (int i = 0; i < count; i++)
    {
        P *p = &particles[i];
        p->x = between(-28, 28); p->y = between(-9, 40); p->z = between(-28, 28);
        p->vx = between(-6, 6); p->vy = between(-2, 9); p->vz = between(-6, 6);
        p->kx = between(-1, 1); p->ky = between(-1, 1); p->kz = between(-1, 1);
        p->r = rnd() < 0.1f ? between(8, 20) : between(0.3f, 6);
        p->born = between(0, 3); p->mass = between(0.05f, 30); p->heat = rnd() < 0.3f ? between(0.3f, 1.6f) : between(0, 0.2f);
        p->life = between(4, 40); p->age = between(0, p->life); p->fadeIn = rnd() < 0.5f ? 0.0f : 0.8f;
        p->kloss = between(0, 1.5f); p->turn = rnd() < 0.02f ? -1.0f : rnd(); p->rate = between(0, 3);
        p->ax = p->x + between(-3, 3); p->ay = p->y + between(-3, 3); p->az = p->z + between(-3, 3);
        p->bx = p->x + between(-3, 3); p->by = p->y + between(-3, 3); p->bz = p->z + between(-3, 3);
        p->cx = p->x + between(-1, 1); p->cy = p->y + between(-1, 1); p->cz = p->z + between(-1, 1);
        p->dx = p->x + between(-1, 1); p->dy = p->y + between(-1, 1); p->dz = p->z + between(-1, 1);
        p->ex = p->x + between(-3, 3); p->ey = p->y + between(-3, 3); p->ez = p->z + between(-3, 3);
        p->fx = p->x + between(-1, 1); p->fy = p->y + between(-1, 1); p->fz = p->z + between(-1, 1);
        p->ar = rnd(); p->ag = p->ar * between(0.5f, 1); p->ab = p->ag * between(0.3f, 1);
        p->flame = rnd() < 0.4f ? rnd() : 0.0f;
        if (rnd() < 0.01f) p->x = NAN;                   // (a particle that is no number at all, which the game can hand over)
    }
    for (int n = 0; n < (int)(sizeof floors / sizeof floors[0]); n++) floors[n] = -10.0f + 3.0f * sinf(n * 0.37f) + 0.01f * (n % 64);
    for (int n = 0; n < BELL_STEPS; n++) bells[n] = (float)(0.5 - 0.5 * cos(2.0 * M_PI * (n + 0.5) / BELL_STEPS));
}

static void run(Lib *l, void *work, int count, int drawn, int sunUp, int halves, int floored, Out *o)
{
    memset(o, 0, sizeof *o);
    o->sun = calloc(G3, 4); o->sky = calloc(G3, 4); o->strain = calloc(A3, 4);
    for (int k = 0; k < CARRIED; k++) o->carry[k] = calloc(A3, 4);
    o->a = calloc(G3, 4); o->b = calloc(G3, 4); o->c = calloc(A3, 4); o->d = calloc(A3, 4); o->e = calloc(A3, 4); o->clear = calloc(A3, 1);
    for (int k = 0; k < 5; k++) o->rest[k] = calloc(A3, 8);
    Grid g;
    memset(&g, 0, sizeof g);
    g.size = sizeof(Grid); g.g = G; g.a = A; g.levels = LEVELS; g.carried = CARRIED;
    g.count = count; g.drawn = drawn; g.sunUp = sunUp; g.halves = halves; g.floored = floored;
    g.particles = particles;
    g.x0 = -32; g.y0 = -12; g.z0 = -32; g.dX = 1.0f; g.dY = 0.9f; g.dZ = 1.1f;
    g.keepX0 = -31; g.keepX1 = 31; g.keepY0 = -11; g.keepY1 = 45; g.keepZ0 = -31; g.keepZ1 = 31; g.fadeX = g.fadeY = g.fadeZ = 0.25f;
    g.leastReach = 0.7f; g.thick = 1.6f; g.hold = 0.6f; g.goesX = 1.0f; g.goesY = 0.4f; g.goesZ = -0.5f; g.ahead = 0.15f;
    g.sunX = 0.3f; g.sunY = 0.8f; g.sunZ = 0.52f; g.repeat = 9.0f; g.skyX = 0.05f; g.skyY = 0.99f; g.skyZ = 0.1f;
    g.thickest = 3.0f; g.thickestFlame = 1.2f; g.something = 0.0015f; g.restFar = 128.0f; g.mostBefore = 16.0f; g.mostAbove = 8.0f;
    g.sunThrough = o->sun; g.skyThrough = o->sky; g.carry = o->carry;
    g.cellsA = o->a; g.cellsB = o->b; g.cellsC = o->c; g.cellsD = o->d; g.cellsRest = o->rest; g.cellsClear = o->clear;
    g.floors = floors; g.cellsE = o->e; g.strain = o->strain; g.bells = bells;
    o->result = call_grid(l, work, &g);
    o->fastest = g.fastest;
}

static long differ(const void *x, const void *y, size_t bytes) { long n = 0; const uint8_t *a = x, *b = y; for (size_t k = 0; k < bytes; k++) n += a[k] != b[k]; return n; }
static double widest(const float *a, const float *b, size_t n) { double w = 0; for (size_t k = 0; k < n; k++) { double d = fabs((double)a[k] - b[k]); if (d > w || (d != d)) w = d != d ? INFINITY : d; } return w; }

static int compare(const char *what, Out *x, Out *y)
{
    long bad = 0;
    bad += x->result != y->result;
    bad += differ(x->sun, y->sun, (size_t)G3 * 4) + differ(x->sky, y->sky, (size_t)G3 * 4) + differ(x->strain, y->strain, (size_t)A3 * 4);
    for (int k = 0; k < CARRIED; k++) bad += differ(x->carry[k], y->carry[k], (size_t)A3 * 4);
    bad += differ(x->a, y->a, (size_t)G3 * 4) + differ(x->b, y->b, (size_t)G3 * 4) + differ(x->c, y->c, (size_t)A3 * 4) + differ(x->d, y->d, (size_t)A3 * 4) + differ(x->e, y->e, (size_t)A3 * 4) + differ(x->clear, y->clear, (size_t)A3);
    for (int k = 0; k < 5; k++) bad += differ(x->rest[k], y->rest[k], (size_t)A3 * 8);
    bad += x->fastest != y->fastest;
    long nonzero = 0;
    for (size_t k = 0; k < (size_t)G3 * 4; k++) nonzero += x->b[k] != 0;
    printf("  %-34s %s (returned %d and %d; bytes that differ: %ld; widest difference in the light %.3g; cells with smoke or flame: %ld)\n", what, bad ? "DIFFERS" : "same", x->result, y->result, bad,
           widest(x->sun, y->sun, G3) > widest(x->sky, y->sky, G3) ? widest(x->sun, y->sun, G3) : widest(x->sky, y->sky, G3), nonzero / 4);
    return bad != 0;
}

static void drop(Out *o)
{
    free(o->sun); free(o->sky); free(o->strain);
    for (int k = 0; k < CARRIED; k++) free(o->carry[k]);
    free(o->a); free(o->b); free(o->c); free(o->d); free(o->e); free(o->clear);
    for (int k = 0; k < 5; k++) free(o->rest[k]);
}

int main(int argc, char **argv)
{
    if (argc != 4) { fprintf(stderr, "usage: %s <Mac x86_64 dylib> <Windows dll> <Linux so>\n", argv[0]); return 2; }
    setvbuf(stdout, NULL, _IONBF, 0);
    Lib libs[3] = { { "Mac (reference)", 0, { 0 } }, { "Windows DLL", 0, { 0 } }, { "Linux .so", 0, { 0 } } };
    load_dylib(argv[1], &libs[0]);
    load_pe(argv[2], &libs[1]);
    load_elf(argv[3], &libs[2]);
    for (int k = 0; k < 3; k++)
        printf("%-16s signature %#x, particle %d bytes, grid %d bytes (this program's: %#x, %d, %d)\n", libs[k].what, call_int(&libs[k], 0), call_int(&libs[k], 1), call_int(&libs[k], 2),
               P_SIGNATURE, (int)sizeof(P), (int)sizeof(Grid));
    void *work[3];
    for (int k = 0; k < 3; k++) if (!(work[k] = call_new(&libs[k]))) { fprintf(stderr, "%s: vfx_new failed\n", libs[k].what); return 1; }
    int failures = 0;
    struct { int count, drawn, sunUp, halves, floored; uint32_t seed; const char *what; } cases[] = {
        { 3000, 1, 1, 0, 1, 1, "volume, sun up, on the ground" },
        { 3000, 0, 1, 0, 1, 2, "sprites (as on Direct3D today)" },
        { 1500, 1, 0, 1, 0, 3, "volume, night, halves, in the air" },
        { 6000, 1, 1, 0, 1, 4, "volume, many particles" },
        { 0, 1, 1, 0, 1, 5, "no particles at all" },
    };
    for (size_t c = 0; c < sizeof cases / sizeof cases[0]; c++)
    {
        make_fire(cases[c].count, cases[c].seed);
        Out out[3];
        for (int k = 0; k < 3; k++) run(&libs[k], work[k], cases[c].count, cases[c].drawn, cases[c].sunUp, cases[c].halves, cases[c].floored, &out[k]);
        printf("%s:\n", cases[c].what);
        failures += compare("Windows DLL against the Mac's", &out[0], &out[1]);
        failures += compare("Linux .so against the Mac's", &out[0], &out[2]);
        for (int k = 0; k < 3; k++) drop(&out[k]);
    }
    // a grid it must refuse: the description of another size
    {
        Grid g; memset(&g, 0, sizeof g); g.size = 12;
        printf("a grid of the wrong size is refused with: %d %d %d\n", call_grid(&libs[0], work[0], &g), call_grid(&libs[1], work[1], &g), call_grid(&libs[2], work[2], &g));
    }
    for (int k = 0; k < 3; k++) call_free(&libs[k], work[k]);
    printf(failures ? "SOME DIFFER\n" : "all the same\n");
    return failures != 0;
}
