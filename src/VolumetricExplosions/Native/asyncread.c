// asyncread: reading a picture back from the graphics card without the game waiting for it, on OpenGL (the Mac's, and
// Linux's), where Unity 2019.4 has no AsyncGPUReadback. Built into Volumetric Explosions' library (beside vfxgrid.c) and
// used by Keystone's Readback (src/Keystone/Readback.cs) for the mods that read pictures back: Natural Weather's shadow at the
// craft, Natural Ocean's white water. Made by the rendering research (research/rendering/native/, where its lab and its
// measurements are; this copy is the mod's), tried there in a lab, then in the game (2026-10-18).
//
// How: the copy is asked for on the thread that holds the game's GL context (Unity's render thread, by GL.IssuePluginEvent
// with ar_event; in the lab, whichever thread has the context): glReadPixels into a pixel buffer of its own (which returns
// at once, the copy queued on the card behind everything already queued) and a fence after it. At later events, each fence
// that has passed is collected: the buffer mapped, its bytes kept, the buffer freed for the next request. The game's main
// thread only writes requests and reads finished results, under a lock; it never waits on the card.
//
// The GL functions are found when first used (dlsym in what the process has loaded: Unity's or the lab's GL), so the library
// itself links to no GL and loads anywhere. Every GL binding it changes is put back as it was, so Unity's own record of the
// GL state stays true.
//
// Built by tools/build/native.sh with vfxgrid.c into one library (Mac and Linux; Windows has Unity's own asynchronous read).
#include <dlfcn.h>
#include <pthread.h>
#include <stdint.h>
#include <string.h>

#define EXPORT __attribute__((visibility("default")))

typedef unsigned int GLenum, GLuint, GLbitfield;
typedef int GLint, GLsizei;
typedef intptr_t GLintptr;
typedef intptr_t GLsizeiptr;
typedef uint64_t GLuint64;
typedef struct __GLsync *GLsync;

#define GL_TEXTURE_2D 0x0DE1
#define GL_RGBA 0x1908
#define GL_FLOAT 0x1406
#define GL_UNSIGNED_BYTE 0x1401
#define GL_READ_FRAMEBUFFER 0x8CA8
#define GL_READ_FRAMEBUFFER_BINDING 0x8CAA
#define GL_COLOR_ATTACHMENT0 0x8CE0
#define GL_PIXEL_PACK_BUFFER 0x88EB
#define GL_PIXEL_PACK_BUFFER_BINDING 0x88ED
#define GL_STREAM_READ 0x88E1
#define GL_MAP_READ_BIT 0x0001
#define GL_SYNC_GPU_COMMANDS_COMPLETE 0x9117
#define GL_ALREADY_SIGNALED 0x911A
#define GL_CONDITION_SATISFIED 0x911C
#define GL_READ_BUFFER 0x0C02
#define GL_PACK_ALIGNMENT 0x0D05
#define GL_PACK_ROW_LENGTH 0x0D02
#define GL_QUERY_RESULT 0x8866
#define GL_QUERY_RESULT_AVAILABLE 0x8867

static struct
{
    void (*GetIntegerv)(GLenum, GLint *);
    void (*GenFramebuffers)(GLsizei, GLuint *);
    void (*BindFramebuffer)(GLenum, GLuint);
    void (*FramebufferTexture2D)(GLenum, GLenum, GLenum, GLuint, GLint);
    void (*ReadBuffer)(GLenum);
    void (*PixelStorei)(GLenum, GLint);
    void (*GenBuffers)(GLsizei, GLuint *);
    void (*BindBuffer)(GLenum, GLuint);
    void (*BufferData)(GLenum, GLsizeiptr, const void *, GLenum);
    void (*ReadPixels)(GLint, GLint, GLsizei, GLsizei, GLenum, GLenum, void *);
    GLsync (*FenceSync)(GLenum, GLbitfield);
    GLenum (*ClientWaitSync)(GLsync, GLbitfield, GLuint64);
    void (*DeleteSync)(GLsync);
    void *(*MapBufferRange)(GLenum, GLintptr, GLsizeiptr, GLbitfield);
    GLbitfield (*UnmapBuffer)(GLenum);
    void (*Flush)(void);
    void (*GenQueries)(GLsizei, GLuint *);
    void (*BeginQuery)(GLenum, GLuint);
    void (*EndQuery)(GLenum);
    void (*GetQueryObjectiv)(GLuint, GLenum, GLint *);
    void (*GetQueryObjectui64v)(GLuint, GLenum, GLuint64 *);
} gl;
static int glFound = 0;      // 0 not looked for, 1 found, -1 missing (then nothing is done)

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
    GET(GetIntegerv, "glGetIntegerv") GET(GenFramebuffers, "glGenFramebuffers") GET(BindFramebuffer, "glBindFramebuffer")
    GET(FramebufferTexture2D, "glFramebufferTexture2D") GET(ReadBuffer, "glReadBuffer") GET(PixelStorei, "glPixelStorei")
    GET(GenBuffers, "glGenBuffers") GET(BindBuffer, "glBindBuffer") GET(BufferData, "glBufferData") GET(ReadPixels, "glReadPixels")
    GET(FenceSync, "glFenceSync") GET(ClientWaitSync, "glClientWaitSync") GET(DeleteSync, "glDeleteSync")
    GET(MapBufferRange, "glMapBufferRange") GET(UnmapBuffer, "glUnmapBuffer") GET(Flush, "glFlush")
    GET(GenQueries, "glGenQueries") GET(BeginQuery, "glBeginQuery") GET(EndQuery, "glEndQuery") GET(GetQueryObjectiv, "glGetQueryObjectiv")
    GET(GetQueryObjectui64v, "glGetQueryObjectui64v")
#undef GET
    glFound = 1;
    return 1;
}

// A slot: one picture read again and again (the clouds' shadow at the craft, the sea's foam). Its request is written by the
// main thread; its copies run on the GL thread; its last finished result is kept for the main thread.
#define SLOTS 8
#define RING 4                 // copies in flight at once for a slot
#define MOST 65536             // bytes a result may hold (a 64 x 64 picture of four floats)
typedef struct
{
    // (the request: written by the main thread under the lock)
    GLuint texture;
    GLint x, y, w, h;
    GLenum type;               // GL_FLOAT (four floats a pixel) or GL_UNSIGNED_BYTE (four bytes)
    int wanted;                // asked for again since the last copy was begun
    // (the copies: the GL thread's alone)
    GLuint buffer[RING];
    GLsync fence[RING];
    int bytes[RING];
    long asked[RING];          // the request's number each copy was begun for
    // (the last finished result: written by the GL thread under the lock)
    unsigned char result[MOST];
    int resultBytes;
    long resultNumber, resultFrames;
    long requests, eventsAtAsk[RING], events;
} Slot;
static Slot slots[SLOTS];
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static GLuint fbo = 0;

// (main thread) Ask for a region of a texture (its GL name, Texture.GetNativeTexturePtr) to be read at the next event. The
// region's top-left is (x, y) in pixels from the texture's first row. Returns 0 if the library cannot do it (then read as before).
EXPORT int ar_request(int slot, unsigned int texture, int x, int y, int w, int h, int floats)
{
    if (slot < 0 || slot >= SLOTS || w <= 0 || h <= 0 || w * h * (floats ? 16 : 4) > MOST) return 0;
    pthread_mutex_lock(&lock);
    Slot *s = &slots[slot];
    s->texture = texture; s->x = x; s->y = y; s->w = w; s->h = h; s->type = floats ? GL_FLOAT : GL_UNSIGNED_BYTE;
    s->wanted = 1;
    s->requests++;
    pthread_mutex_unlock(&lock);
    return glFound >= 0;
}

// (main thread) The last finished result of a slot: copied into out (at most room bytes), its request's number returned (0:
// none yet). frames: how many events passed between its copy being begun and its being collected.
EXPORT long ar_result(int slot, void *out, int room, long *frames)
{
    if (slot < 0 || slot >= SLOTS) return 0;
    pthread_mutex_lock(&lock);
    Slot *s = &slots[slot];
    long n = s->resultNumber;
    if (n > 0 && out) memcpy(out, s->result, s->resultBytes < room ? s->resultBytes : room);
    if (frames) *frames = s->resultFrames;
    pthread_mutex_unlock(&lock);
    return n;
}

// (GL thread) Collect what has finished, then begin what has been asked for. Called once a frame, by GL.IssuePluginEvent in
// the game, directly in the lab.
static void step(void)
{
    if (!findGL()) return;
    GLint readFbo = 0, packBuffer = 0, readBuffer = 0, packAlign = 4, packRow = 0;
    gl.GetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &readFbo);
    gl.GetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &packBuffer);
    gl.GetIntegerv(GL_PACK_ALIGNMENT, &packAlign);
    gl.GetIntegerv(GL_PACK_ROW_LENGTH, &packRow);
    for (int i = 0; i < SLOTS; i++)
    {
        Slot *s = &slots[i];
        s->events++;
        // (collect: the oldest copies first, each only if its fence has passed: never waits)
        for (int r = 0; r < RING; r++)
        {
            if (!s->fence[r]) continue;
            GLenum got = gl.ClientWaitSync(s->fence[r], 0, 0);
            if (got != GL_ALREADY_SIGNALED && got != GL_CONDITION_SATISFIED) continue;
            gl.DeleteSync(s->fence[r]); s->fence[r] = 0;
            gl.BindBuffer(GL_PIXEL_PACK_BUFFER, s->buffer[r]);
            void *p = gl.MapBufferRange(GL_PIXEL_PACK_BUFFER, 0, s->bytes[r], GL_MAP_READ_BIT);
            if (p)
            {
                pthread_mutex_lock(&lock);
                if (s->asked[r] > s->resultNumber)
                {
                    memcpy(s->result, p, s->bytes[r]); s->resultBytes = s->bytes[r];
                    s->resultNumber = s->asked[r]; s->resultFrames = s->events - s->eventsAtAsk[r];
                }
                pthread_mutex_unlock(&lock);
                gl.UnmapBuffer(GL_PIXEL_PACK_BUFFER);
            }
        }
        // (begin: into the first buffer not in flight)
        pthread_mutex_lock(&lock);
        int want = s->wanted && s->texture;
        GLuint texture = s->texture; GLint x = s->x, y = s->y, w = s->w, h = s->h; GLenum type = s->type; long number = s->requests;
        s->wanted = 0;
        pthread_mutex_unlock(&lock);
        if (!want) continue;
        int r = 0;
        while (r < RING && s->fence[r]) r++;
        if (r == RING) continue;                       // (all in flight: this request is skipped, the next will come)
        if (!fbo) gl.GenFramebuffers(1, &fbo);
        if (!s->buffer[r]) gl.GenBuffers(1, &s->buffer[r]);
        int bytes = w * h * (type == GL_FLOAT ? 16 : 4);
        gl.BindFramebuffer(GL_READ_FRAMEBUFFER, fbo);
        gl.FramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, texture, 0);
        gl.GetIntegerv(GL_READ_BUFFER, &readBuffer);
        gl.ReadBuffer(GL_COLOR_ATTACHMENT0);
        gl.BindBuffer(GL_PIXEL_PACK_BUFFER, s->buffer[r]);
        if (bytes > s->bytes[r]) gl.BufferData(GL_PIXEL_PACK_BUFFER, bytes, 0, GL_STREAM_READ);
        s->bytes[r] = bytes > s->bytes[r] ? bytes : s->bytes[r];
        gl.PixelStorei(GL_PACK_ALIGNMENT, 4); gl.PixelStorei(GL_PACK_ROW_LENGTH, 0);
        gl.ReadPixels(x, y, w, h, GL_RGBA, type, 0);
        s->bytes[r] = bytes;
        s->fence[r] = gl.FenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        s->asked[r] = number; s->eventsAtAsk[r] = s->events;
        gl.FramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, 0, 0);
        gl.ReadBuffer(readBuffer);
    }
    // (put back what was bound, as Unity left it)
    gl.BindFramebuffer(GL_READ_FRAMEBUFFER, (GLuint)readFbo);
    gl.BindBuffer(GL_PIXEL_PACK_BUFFER, (GLuint)packBuffer);
    gl.PixelStorei(GL_PACK_ALIGNMENT, packAlign); gl.PixelStorei(GL_PACK_ROW_LENGTH, packRow);
    gl.Flush();                                        // (so that the fences are sent, and pass)
}

// The render event, as GL.IssuePluginEvent wants it: void (int).
static void onEvent(int id) { (void)id; step(); }
EXPORT void *ar_event(void) { return (void *)onEvent; }

// ---------------------------------------------------------------- the card's own clock: how long each pass takes on it
// A section is a stretch of the card's work between two render events from a command buffer:
// CommandBuffer.IssuePluginEvent(ar_section_event(), id) where it begins, and with 1000 + id where it ends (id 0 to
// SECTIONS-1). Each is timed by a GL_TIME_ELAPSED query; sections must not overlap (a begin while another is open is let
// go untimed). Each keeps FRAMES queries in flight, collected without waiting (QUERY_RESULT_AVAILABLE); ar_ms(id) gives
// the newest, in milliseconds. (Timestamps, glQueryCounter, would let sections nest, but on this Mac's OpenGL, which runs on
// Metal, every timestamp in one batch of the card's work reads the same: measured, 0.00 ms across a 10 ms drawing.)
#define SECTIONS 64
#define FRAMES 4
#define GL_TIME_ELAPSED 0x88BF
static GLuint secQ[SECTIONS][FRAMES];
static int secPending[SECTIONS][FRAMES];
static long secCount[SECTIONS];
static double secMs[SECTIONS];
static double secSum[SECTIONS];         // (every time collected since the last ar_take, added up, and how many)
static int secN[SECTIONS];
static int secOpen = -1, secOpenSlot = -1;

static void section(int code)
{
    if (!findGL()) return;
    int id = code >= 1000 ? code - 1000 : code;
    if (id < 0 || id >= SECTIONS) return;
    if (code < 1000)
    {
        if (secOpen >= 0) return;                // (one at a time)
        for (int f = 0; f < FRAMES; f++)         // (collect, without waiting)
        {
            if (!secPending[id][f]) continue;
            GLint ready = 0;
            gl.GetQueryObjectiv(secQ[id][f], GL_QUERY_RESULT_AVAILABLE, &ready);
            if (!ready) continue;
            GLuint64 ns = 0;
            gl.GetQueryObjectui64v(secQ[id][f], GL_QUERY_RESULT, &ns);
            pthread_mutex_lock(&lock); secMs[id] = (double)ns / 1e6; secSum[id] += (double)ns / 1e6; secN[id]++; pthread_mutex_unlock(&lock);
            secPending[id][f] = 0;
        }
        int f = (int)(secCount[id]++ % FRAMES);
        if (secPending[id][f]) return;           // (still in flight from FRAMES ago: this one untimed)
        if (!secQ[id][f]) gl.GenQueries(1, &secQ[id][f]);
        gl.BeginQuery(GL_TIME_ELAPSED, secQ[id][f]);
        secOpen = id; secOpenSlot = f;
    }
    else if (secOpen == id)
    {
        gl.EndQuery(GL_TIME_ELAPSED);
        secPending[id][secOpenSlot] = 1;
        secOpen = -1;
    }
}
static void onSection(int code) { section(code); }
EXPORT void *ar_section_event(void) { return (void *)onSection; }
EXPORT void ar_section(int code) { section(code); }   // (lab: on the thread that holds the context)

// (main thread) The card's time in a section, the newest collected, ms; -1: none yet.
EXPORT double ar_ms(int id)
{
    if (id < 0 || id >= SECTIONS) return -1.0;
    pthread_mutex_lock(&lock);
    double ms = secCount[id] > 0 && secMs[id] > 0.0 ? secMs[id] : -1.0;
    pthread_mutex_unlock(&lock);
    return ms;
}

// (lab) The read-backs' step, called directly on the thread that holds the context.
EXPORT void ar_step(void) { step(); }

// 1 once the GL functions are found, -1 if they are not there, 0 before the first event.
EXPORT int ar_ready(void) { return glFound; }
// (main thread) Every time a section has taken since the last call, added up, and how many: then begun afresh. For means over
// a stretch of frames (research/rendering/ingame/GpuStages).
EXPORT void ar_take(int id, double *sum, int *count)
{
    *sum = 0.0; *count = 0;
    if (id < 0 || id >= SECTIONS) return;
    pthread_mutex_lock(&lock);
    *sum = secSum[id]; *count = secN[id]; secSum[id] = 0.0; secN[id] = 0;
    pthread_mutex_unlock(&lock);
}

EXPORT int ar_version(void) { return 3; }
