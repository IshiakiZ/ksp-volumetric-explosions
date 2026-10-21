// jitlog: which methods the game's runtime compiles, when, on which thread and how long each takes. A development tool for
// finding the game's first-time stalls (2026-10-18): the first lift-off of a game stood still for over 200 ms, spread over a
// dozen mods and the game itself, and macOS's sampler said much of that was Mono compiling code, but not whose.
//
// How: Mono (Unity 2019.4's) has a profiler interface that can be put in while it runs (mono_profiler_create, with callbacks
// for a method's compiling begun and done). Its functions are found in the process by dlsym, so this links to nothing of
// Mono's and loads anywhere; it does nothing until jl_start. Each method compiled is noted, its full name, when it began
// and how long it took (from its beginning to its end, so a method whose compiling made another be compiled holds that
// one's time too, and says how deep it was), and whether on the game's main thread. The game's own frames are marked too
// (jl_frame, once a frame at its start), so that what was compiled can be put into the frame it held up. jl_take writes all
// of it out as text, to a file.
//
// Built for the development build only (tools/dev/jitlog.sh), into ~/.ksp-ai-bridge/, and never into any mod's download.
#include <dlfcn.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define EXPORT __attribute__((visibility("default")))

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoMethod MonoMethod;
typedef struct _MonoJitInfo MonoJitInfo;
typedef void (*JitBegin)(MonoProfiler *, MonoMethod *);
typedef void (*JitDone)(MonoProfiler *, MonoMethod *, MonoJitInfo *);
typedef void (*JitFailed)(MonoProfiler *, MonoMethod *);

struct _MonoProfiler { int unused; };
static struct _MonoProfiler me;
static MonoProfilerHandle handle;
static void (*setBegin)(MonoProfilerHandle, JitBegin);
static void (*setDone)(MonoProfilerHandle, JitDone);
static void (*setFailed)(MonoProfilerHandle, JitFailed);
static char *(*fullName)(MonoMethod *, int);
static void (*monoFree)(void *);

static volatile int on;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static double tick;                         // milliseconds in one of mach_absolute_time's
static uint64_t zero;                       // when noting began

// ---- what was compiled
#define MOST 60000
#define NAMES (MOST * 120)
typedef struct { double at, took; int depth, main, name; } Noted;
static Noted *noted;
static char *names;
static int count, used, lost;

// ---- the frames' beginnings
#define FRAMES 20000
static double *frames;
static int frameCount;

// ---- what is being compiled on each thread just now (begun and not yet done)
#define DEEPEST 32
typedef struct { MonoMethod *method[DEEPEST]; uint64_t began[DEEPEST]; int depth; } Stack;
static __thread Stack stack;

static double Ms(uint64_t t) { return (double)(t - zero) * tick; }

static void Begun(MonoProfiler *prof, MonoMethod *method)
{
    (void)prof;
    if (!on) return;
    int d = stack.depth++;
    if (d < DEEPEST) { stack.method[d] = method; stack.began[d] = mach_absolute_time(); }
}

static void Ended(MonoMethod *method, int failed)
{
    if (stack.depth <= 0) return;
    int d = --stack.depth;
    if (d >= DEEPEST || stack.method[d] != method) return;
    uint64_t end = mach_absolute_time();
    if (!on) return;
    // (the name is made after the time is taken: it is not the compiling's)
    char *name = fullName != NULL ? fullName(method, 1) : NULL;
    const char *n = name != NULL ? name : "?";
    size_t length = strlen(n);
    pthread_mutex_lock(&lock);
    if (count < MOST && used + (int)length + 8 < NAMES)
    {
        Noted *x = &noted[count++];
        x->at = Ms(stack.began[d]);
        x->took = (double)(end - stack.began[d]) * tick;
        x->depth = d;
        x->main = pthread_main_np();
        x->name = used;
        if (failed) { memcpy(names + used, "FAILED ", 7); used += 7; }
        memcpy(names + used, n, length);
        used += (int)length;
        names[used++] = 0;
    }
    else lost++;
    pthread_mutex_unlock(&lock);
    if (name != NULL && monoFree != NULL) monoFree(name);
}

static void Done(MonoProfiler *prof, MonoMethod *method, MonoJitInfo *info) { (void)prof; (void)info; Ended(method, 0); }
static void Failed(MonoProfiler *prof, MonoMethod *method) { (void)prof; Ended(method, 1); }

static void *Find(const char *name) { return dlsym(RTLD_DEFAULT, name); }

/// Begin noting (afresh). Gives 1, or 0 if Mono's profiler interface is not in this process.
EXPORT int jl_start(void)
{
    if (handle == NULL)
    {
        MonoProfilerHandle (*create)(MonoProfiler *) = (MonoProfilerHandle (*)(MonoProfiler *))Find("mono_profiler_create");
        setBegin = (void (*)(MonoProfilerHandle, JitBegin))Find("mono_profiler_set_jit_begin_callback");
        setDone = (void (*)(MonoProfilerHandle, JitDone))Find("mono_profiler_set_jit_done_callback");
        setFailed = (void (*)(MonoProfilerHandle, JitFailed))Find("mono_profiler_set_jit_failed_callback");
        fullName = (char *(*)(MonoMethod *, int))Find("mono_method_full_name");
        monoFree = (void (*)(void *))Find("mono_free");
        if (create == NULL || setBegin == NULL || setDone == NULL) return 0;
        mach_timebase_info_data_t base;
        mach_timebase_info(&base);
        tick = (double)base.numer / (double)base.denom / 1e6;
        noted = (Noted *)calloc(MOST, sizeof(Noted));
        names = (char *)malloc(NAMES);
        frames = (double *)calloc(FRAMES, sizeof(double));
        if (noted == NULL || names == NULL || frames == NULL) return 0;
        handle = create(&me);
        setBegin(handle, Begun);
        setDone(handle, Done);
        if (setFailed != NULL) setFailed(handle, Failed);
    }
    pthread_mutex_lock(&lock);
    count = used = lost = frameCount = 0;
    zero = mach_absolute_time();
    pthread_mutex_unlock(&lock);
    on = 1;
    return 1;
}

/// Stop noting (what was noted is kept for jl_take).
EXPORT void jl_stop(void) { on = 0; }

/// A frame begins (called by the game once a frame, at its start).
EXPORT void jl_frame(void)
{
    if (!on) return;
    double now = Ms(mach_absolute_time());
    pthread_mutex_lock(&lock);
    if (frameCount < FRAMES) frames[frameCount++] = now;
    pthread_mutex_unlock(&lock);
}

/// Write all that was noted to a file: "frame <ms>" for each frame's beginning, then "<at ms> <took ms> <depth> <main 1/0> <name>"
/// for each method compiled. Gives how many methods, or -1 if the file could not be written.
EXPORT int jl_take(const char *path)
{
    FILE *f = fopen(path, "w");
    if (f == NULL) return -1;
    pthread_mutex_lock(&lock);
    fprintf(f, "# jitlog: %d methods compiled, %d frames, %d not noted (no room)\n", count, frameCount, lost);
    for (int k = 0; k < frameCount; k++) fprintf(f, "frame %.3f\n", frames[k]);
    for (int k = 0; k < count; k++) fprintf(f, "%.3f %.3f %d %d %s\n", noted[k].at, noted[k].took, noted[k].depth, noted[k].main, names + noted[k].name);
    int n = count;
    pthread_mutex_unlock(&lock);
    fclose(f);
    return n;
}
