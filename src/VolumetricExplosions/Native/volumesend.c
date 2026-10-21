// volumesend: a patch's grids handed to the graphics card without the game's main thread waiting for it, on OpenGL (the Mac's,
// and Linux's). Built into Volumetric Explosions' library beside vfxgrid.c and asyncread.c; used by Sending.cs (2026-10-18).
//
// Why: each time a patch of smoke shows a new grid, its eleven volume textures (3.7 MB) went to the card through Unity's own
// Texture3D.Apply. That copies the grid into the queue of commands Unity's render thread works through, and while a rocket climbs
// a few patches show forty grids a second between them: 150 MB a second through that queue. Mostly that took a fraction of a
// millisecond a texture; but whenever the queue was full, the game's thread waited at whatever texture it was sending, for 10 to
// 18 ms, several times a second (measured in the game, 2026-10-18).
//
// How: the game's thread copies each grid into a block of the library's own (vs_send: a memory copy, a quarter of a millisecond
// for a whole grid) and queues it; a texture whose last grid has not gone yet has that one replaced, as only the newest is
// wanted. Once a frame, on the thread that holds the game's GL context (GL.IssuePluginEvent with vs_event), every queued block is
// sent with glTexSubImage3D. The GL functions are found when first used (dlsym), so the library links to no GL and loads
// anywhere; every GL binding and setting changed is put back as it was, so Unity's own record of the GL state stays true.
//
// Two ways of sending (vs_mode). 0, straight from the library's blocks: GL copies each block before glTexSubImage3D returns,
// and where the texture is still being read by the card for the frame before, the driver may wait for the card to finish
// with it (a climb's worst event took 37.8 ms of the render thread, 2026-10-18, against 0.8 ms on average). 1, through a
// buffer of the card's own (a pixel unpack buffer, made anew each frame by orphaning): the blocks are copied into it, and
// glTexSubImage3D from it is a copy the card itself makes in its own order, after whatever reads the texture before it,
// with nothing for the render thread to wait for. The slowest events are kept with what they sent (vs_slow), to tell the
// two apart in the game.
#include <dlfcn.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define EXPORT __attribute__((visibility("default")))

typedef unsigned int GLenum, GLuint, GLbitfield;
typedef int GLint, GLsizei;
typedef unsigned char GLboolean;
typedef long GLsizeiptr, GLintptr;

#define GL_TEXTURE_3D 0x806F
#define GL_TEXTURE_BINDING_3D 0x806A
#define GL_PIXEL_UNPACK_BUFFER 0x88EC
#define GL_PIXEL_UNPACK_BUFFER_BINDING 0x88EF
#define GL_UNPACK_ALIGNMENT 0x0CF5
#define GL_UNPACK_ROW_LENGTH 0x0CF2
#define GL_UNPACK_SKIP_ROWS 0x0CF3
#define GL_UNPACK_SKIP_PIXELS 0x0CF4
#define GL_UNPACK_IMAGE_HEIGHT 0x806E
#define GL_UNPACK_SKIP_IMAGES 0x806D
#define GL_RGBA 0x1908
#define GL_RED 0x1903
#define GL_UNSIGNED_BYTE 0x1401
#define GL_UNSIGNED_SHORT 0x1403
#define GL_HALF_FLOAT 0x140B
#define GL_NO_ERROR 0
#define GL_STREAM_DRAW 0x88E0
#define GL_MAP_WRITE_BIT 0x0002
#define GL_MAP_INVALIDATE_BUFFER_BIT 0x0008

static struct
{
    void (*GetIntegerv)(GLenum, GLint *);
    void (*BindTexture)(GLenum, GLuint);
    void (*BindBuffer)(GLenum, GLuint);
    void (*PixelStorei)(GLenum, GLint);
    void (*TexSubImage3D)(GLenum, GLint, GLint, GLint, GLint, GLsizei, GLsizei, GLsizei, GLenum, GLenum, const void *);
    GLenum (*GetError)(void);
    // (for sending through a buffer of the card's own: mode 1)
    void (*GenBuffers)(GLsizei, GLuint *);
    void (*BufferData)(GLenum, GLsizeiptr, const void *, GLenum);
    void *(*MapBufferRange)(GLenum, GLintptr, GLsizeiptr, GLbitfield);
    GLboolean (*UnmapBuffer)(GLenum);
} gl;
static int glFound = 0;      // 0 not looked for, 1 found, -1 missing (then nothing is queued)
static int buffersFound;     // whether the card's own buffers can be used (mode 1)

static void *find(const char *name)
{
    void *f = dlsym(RTLD_DEFAULT, name);
#if defined(__APPLE__)
    if (!f)
    {
        static void *framework = 0;
        if (!framework) framework = dlopen("/System/Library/Frameworks/OpenGL.framework/OpenGL", RTLD_LAZY | RTLD_GLOBAL);
        if (framework) f = dlsym(framework, name);
    }
#endif
    return f;
}

static int findGL(void)
{
    if (glFound) return glFound > 0;
#define GET(field, name) gl.field = find(name); if (!gl.field) { glFound = -1; return 0; }
    GET(GetIntegerv, "glGetIntegerv") GET(BindTexture, "glBindTexture") GET(BindBuffer, "glBindBuffer") GET(PixelStorei, "glPixelStorei")
    GET(TexSubImage3D, "glTexSubImage3D") GET(GetError, "glGetError")
#undef GET
    gl.GenBuffers = find("glGenBuffers"); gl.BufferData = find("glBufferData");
    gl.MapBufferRange = find("glMapBufferRange"); gl.UnmapBuffer = find("glUnmapBuffer");
    buffersFound = gl.GenBuffers && gl.BufferData && gl.MapBufferRange && gl.UnmapBuffer;
    glFound = 1;
    return 1;
}

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}

// ---- what is waiting to go, and the blocks it is kept in
#define MOST 512             // textures waiting at once (a patch has eleven)
#define SPARE 96             // blocks kept for use again
typedef struct { GLuint texture; int side, kind, bytes; void *block; } Entry;
static Entry queue[MOST], taken[MOST];
static int queued;
static void *spare[SPARE];
static int spareBytes[SPARE], spares;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
// (for the development build's report)
static long sent, replaced, refused, errors, events;
static double busiest, spent;
// how grids are sent (see the top): 0 straight from the blocks, 1 through a buffer of the card's own
static volatile int mode;
static GLuint unpack;        // that buffer (mode 1), made when first used
// (the slowest events, kept until asked for: when, how long, how many textures and bytes, the slowest texture's time and size,
// and how long before it that texture had last been sent; GL thread only but for vs_slow, under the lock)
#define SLOW 16
typedef struct { double at, took, textures, bytes, slowest, slowestBytes, since, way; } Slow;
static Slow slowest[SLOW];
static int slows;
#define SEEN 4096
static GLuint seenName[SEEN];
static double seenAt[SEEN];

// (under the lock) A block of so many bytes: one kept, or a new one.
static void *takeBlock(int bytes)
{
    for (int i = 0; i < spares; i++)
        if (spareBytes[i] == bytes)
        {
            void *b = spare[i];
            spare[i] = spare[spares - 1]; spareBytes[i] = spareBytes[spares - 1]; spares--;
            return b;
        }
    return malloc((size_t)bytes);
}

// (under the lock) A block given back: kept, or freed where enough are kept.
static void giveBlock(void *b, int bytes)
{
    if (!b) return;
    if (spares < SPARE) { spare[spares] = b; spareBytes[spares] = bytes; spares++; }
    else free(b);
}

// (main thread) Queue a grid for a volume texture (its GL name, Texture.GetNativeTexturePtr), side x side x side texels of a
// kind: 0 four bytes (RGBA), 1 one byte (red), 2 four sixteen-bit numbers 0 to 1 (RGBA16), 3 four half floats. The grid is copied
// at once: data may be changed as soon as this returns. Returns 1 if queued, 0 if not (then send it as before, through Unity).
EXPORT int vs_send(unsigned int texture, int side, int kind, const void *data, int bytes)
{
    if (glFound < 0 || texture == 0 || data == 0 || side <= 0 || kind < 0 || kind > 3) return 0;
    int per = kind == 0 ? 4 : kind == 1 ? 1 : 8;
    if (bytes != side * side * side * per) return 0;
    pthread_mutex_lock(&lock);
    Entry *e = 0;
    for (int i = 0; i < queued; i++) if (queue[i].texture == texture) { e = &queue[i]; break; }
    if (e && e->bytes == bytes) replaced++;
    else if (e) { giveBlock(e->block, e->bytes); e->block = takeBlock(bytes); }
    else
    {
        if (queued == MOST) { refused++; pthread_mutex_unlock(&lock); return 0; }
        e = &queue[queued];
        e->block = takeBlock(bytes);
        if (!e->block) { refused++; pthread_mutex_unlock(&lock); return 0; }
        queued++;
    }
    if (!e->block) { e->bytes = 0; e->texture = 0; refused++; pthread_mutex_unlock(&lock); return 0; }
    e->texture = texture; e->side = side; e->kind = kind; e->bytes = bytes;
    memcpy(e->block, data, (size_t)bytes);
    pthread_mutex_unlock(&lock);
    return 1;
}

// (GL thread) Send everything queued. Called once a frame, by GL.IssuePluginEvent.
static void sendAll(void)
{
    if (!findGL()) return;
    pthread_mutex_lock(&lock);
    int n = queued;
    memcpy(taken, queue, sizeof(Entry) * (size_t)n);
    queued = 0;
    events++;
    pthread_mutex_unlock(&lock);
    if (n == 0) return;
    double began = now();
    GLint texture3d = 0, unpackBuffer = 0, align = 4, rowLength = 0, skipRows = 0, skipPixels = 0, imageHeight = 0, skipImages = 0;
    gl.GetIntegerv(GL_TEXTURE_BINDING_3D, &texture3d);
    gl.GetIntegerv(GL_PIXEL_UNPACK_BUFFER_BINDING, &unpackBuffer);
    gl.GetIntegerv(GL_UNPACK_ALIGNMENT, &align);
    gl.GetIntegerv(GL_UNPACK_ROW_LENGTH, &rowLength);
    gl.GetIntegerv(GL_UNPACK_SKIP_ROWS, &skipRows);
    gl.GetIntegerv(GL_UNPACK_SKIP_PIXELS, &skipPixels);
    gl.GetIntegerv(GL_UNPACK_IMAGE_HEIGHT, &imageHeight);
    gl.GetIntegerv(GL_UNPACK_SKIP_IMAGES, &skipImages);
    while (gl.GetError() != GL_NO_ERROR) { }          // (what was there before is not this library's)
    gl.PixelStorei(GL_UNPACK_ALIGNMENT, 1);
    gl.PixelStorei(GL_UNPACK_ROW_LENGTH, 0); gl.PixelStorei(GL_UNPACK_SKIP_ROWS, 0); gl.PixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
    gl.PixelStorei(GL_UNPACK_IMAGE_HEIGHT, 0); gl.PixelStorei(GL_UNPACK_SKIP_IMAGES, 0);
    // (mode 1: every block copied into one buffer of the card's own, made anew for this frame (the old one, which the card may
    // still be copying from, is left to it: "orphaned"), each block at a place a multiple of 256 bytes in; then the textures sent
    // from the places in it. Where the buffer cannot be had, the blocks are sent straight, as in mode 0.)
    int way = 0;
    if (mode == 1 && buffersFound)
    {
        long total = 0;
        for (int i = 0; i < n; i++) if (taken[i].texture && taken[i].block) total += ((long)taken[i].bytes + 255) & ~255L;
        if (!unpack) gl.GenBuffers(1, &unpack);
        if (unpack && total > 0)
        {
            gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, unpack);
            gl.BufferData(GL_PIXEL_UNPACK_BUFFER, (GLsizeiptr)total, 0, GL_STREAM_DRAW);
            char *into = gl.MapBufferRange(GL_PIXEL_UNPACK_BUFFER, 0, (GLsizeiptr)total, GL_MAP_WRITE_BIT | GL_MAP_INVALIDATE_BUFFER_BIT);
            if (into)
            {
                long at = 0;
                for (int i = 0; i < n; i++)
                {
                    Entry *e = &taken[i];
                    if (!e->texture || !e->block) continue;
                    memcpy(into + at, e->block, (size_t)e->bytes);
                    at += ((long)e->bytes + 255) & ~255L;
                }
                if (gl.UnmapBuffer(GL_PIXEL_UNPACK_BUFFER)) way = 1;
            }
            if (!way) gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        }
    }
    if (!way && unpackBuffer) gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
    long bad = 0, at = 0, bytes = 0;
    double slowestTook = 0.0, slowestSince = -1.0;
    int slowestBytes = 0;
    for (int i = 0; i < n; i++)
    {
        Entry *e = &taken[i];
        if (!e->texture || !e->block) continue;
        GLenum format = e->kind == 1 ? GL_RED : GL_RGBA;
        GLenum type = e->kind <= 1 ? GL_UNSIGNED_BYTE : e->kind == 2 ? GL_UNSIGNED_SHORT : GL_HALF_FLOAT;
        double t0 = now();
        gl.BindTexture(GL_TEXTURE_3D, e->texture);
        gl.TexSubImage3D(GL_TEXTURE_3D, 0, 0, 0, 0, e->side, e->side, e->side, format, type, way ? (const void *)(uintptr_t)at : e->block);
        if (gl.GetError() != GL_NO_ERROR) bad++;
        double t1 = now();
        // (when this texture was sent last: the table is by its name, and only this thread uses it)
        int k = (int)(e->texture & (SEEN - 1));
        double since = seenName[k] == e->texture ? t1 - seenAt[k] : -1.0;
        seenName[k] = e->texture; seenAt[k] = t1;
        if (t1 - t0 > slowestTook) { slowestTook = t1 - t0; slowestBytes = e->bytes; slowestSince = since; }
        at += ((long)e->bytes + 255) & ~255L;
        bytes += e->bytes;
    }
    gl.BindTexture(GL_TEXTURE_3D, (GLuint)texture3d);
    if (way || unpackBuffer) gl.BindBuffer(GL_PIXEL_UNPACK_BUFFER, (GLuint)unpackBuffer);
    gl.PixelStorei(GL_UNPACK_ALIGNMENT, align);
    gl.PixelStorei(GL_UNPACK_ROW_LENGTH, rowLength); gl.PixelStorei(GL_UNPACK_SKIP_ROWS, skipRows); gl.PixelStorei(GL_UNPACK_SKIP_PIXELS, skipPixels);
    gl.PixelStorei(GL_UNPACK_IMAGE_HEIGHT, imageHeight); gl.PixelStorei(GL_UNPACK_SKIP_IMAGES, skipImages);
    double ended = now(), took = ended - began;
    pthread_mutex_lock(&lock);
    for (int i = 0; i < n; i++) giveBlock(taken[i].block, taken[i].bytes);
    sent += n; errors += bad; spent += took;
    if (took > busiest) busiest = took;
    // (kept among the slowest: in place of the quickest kept, once SLOW are)
    int put = slows < SLOW ? slows++ : -1;
    if (put < 0) { put = 0; for (int i = 1; i < SLOW; i++) if (slowest[i].took < slowest[put].took) put = i; if (slowest[put].took >= took) put = -1; }
    if (put >= 0)
    {
        Slow s = { ended, took, (double)n, (double)bytes, slowestTook, (double)slowestBytes, slowestSince, (double)way };
        slowest[put] = s;
    }
    pthread_mutex_unlock(&lock);
}

static void onEvent(int id) { (void)id; sendAll(); }

// The render event, as GL.IssuePluginEvent wants it: void (int).
EXPORT void *vs_event(void) { return (void *)onEvent; }

// (main thread) Whether the library can send: 1 yes, 0 not known yet (no event has run), -1 no (no GL here).
EXPORT int vs_ready(void) { return glFound; }

// (main thread) What has been sent since the last time this was asked: textures, of them refused or replaced before they went,
// GL errors, events, and the render thread's time in all and at worst (ms). Fills 8 numbers.
EXPORT void vs_take(double *out)
{
    pthread_mutex_lock(&lock);
    out[0] = (double)sent; out[1] = (double)refused; out[2] = (double)replaced; out[3] = (double)errors;
    out[4] = (double)events; out[5] = spent; out[6] = busiest; out[7] = (double)queued;
    sent = refused = replaced = errors = events = 0; spent = busiest = 0.0;
    pthread_mutex_unlock(&lock);
}

// (main thread) How grids are sent from now on: 0 straight from the library's blocks, 1 through a buffer of the card's own (see
// the top). Returns the way now set; 1 asked for where the buffers are missing still tries them, and falls back frame by frame.
EXPORT int vs_mode(int way) { mode = way == 1 ? 1 : 0; return mode; }

// (main thread) The slowest events since this was last asked, slowest first, up to most of them: for each, eight numbers (when it
// ended, ms on the library's clock; how long it took; textures; bytes; the slowest texture's ms and bytes; how long before that
// texture had last been sent, ms, or -1; 1 if sent through the card's buffer). The clock's time now first. Returns how many.
EXPORT int vs_slow(double *out, int most)
{
    pthread_mutex_lock(&lock);
    out[0] = now();
    int n = 0;
    while (n < most && slows > 0)
    {
        int best = 0;
        for (int i = 1; i < slows; i++) if (slowest[i].took > slowest[best].took) best = i;
        Slow s = slowest[best];
        slowest[best] = slowest[--slows];
        double *o = out + 1 + n * 8;
        o[0] = s.at; o[1] = s.took; o[2] = s.textures; o[3] = s.bytes; o[4] = s.slowest; o[5] = s.slowestBytes; o[6] = s.since; o[7] = s.way;
        n++;
    }
    slows = 0;
    pthread_mutex_unlock(&lock);
    return n;
}
