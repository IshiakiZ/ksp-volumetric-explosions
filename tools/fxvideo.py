#!/usr/bin/env python3
"""Render the Volumetric Explosions showcase video from the running game.

    FXDEV=1 ./build.sh dev          the development build, with the explosions mod folded in
    python3 tools/fxvideo.py out.mp4 [scene ...]      (naming scenes renders only those again and joins the whole film)

Needs KSP running with the "AI Sandbox" test save (its quicksaves are the sets) and ffmpeg. Each scene
loads a quicksave, aims the camera, and plays a list of cues while the plugin writes every frame to
disk, frame-locked, so the result is smooth however slowly the game runs meanwhile. Then the same cues
are played again at normal speed to record the sound, and ffmpeg joins the two.
"""

import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "mcp"))
import ksp_mcp  # noqa: E402

WORK = os.environ.get("FXVIDEO_WORK") or os.path.join(os.path.expanduser("~"), ".ksp-ai-bridge", "fxvideo")
WIDTH, HEIGHT, FPS = 1920, 1080, 60


def blast(at, power, what=None, east=-40.0, north=0.0, up=1.5, surface=True, **more):
    """An explosion cue. 'what' describes a made-up destroyed part, e.g. "fuel=180 oxidizer=220 cause=crash speed=45"."""
    cue = dict({"at": at, "do": "explode", "power": power, "east": east, "north": north, "up": up, "surface": surface}, **more)
    if what:
        cue["what"] = what
    return cue


def caption(at, text):
    return {"at": at, "do": "caption", "text": text}


PAD = {"east": -40, "north": 0, "up": 10, "surface": True}          # where the pad scenes look
FUEL = "fuel=180 oxidizer=220"
SCENES = {
    # The same blast twice from the same place: the game's own, then the mod's.
    "compare": {
        "save": "fx_pad", "seconds": 15, "look_at": PAD, "camera": {"distance": 62, "pitch": 6, "heading": 15},
        "cues": [{"at": 0, "do": "fx", "off": True}, caption(0, "The game's own explosion"), blast(1.0, 1.0),
                 {"at": 4.2, "do": "fx", "off": False}, caption(4.2, "Volumetric Explosions: 400 kg of fuel and oxidiser"), blast(5.4, 1.0, FUEL)],
    },
    # One fuel fire close up, the camera walking round it: the smoke is a volume, not pictures.
    "close": {
        "save": "fx_pad", "seconds": 13, "look_at": PAD, "camera": {"distance": 40, "pitch": 5, "heading": 15}, "orbit_deg_s": 4.0,
        "cues": [caption(0, "Fire and smoke are a volume the camera can walk round"), blast(0.6, 1.0, FUEL)],
    },
    # What blew up decides what it looks like.
    "kinds": {
        "save": "fx_pad", "seconds": 18, "look_at": {"east": -55, "north": 0, "up": 9, "surface": True}, "camera": {"distance": 105, "pitch": 7, "heading": 15},
        "cues": [caption(0, "Solid propellant"), blast(0.5, 0.8, "solid=400", east=-55, north=-45),
                 caption(4.0, "Monopropellant"), blast(4.2, 0.8, "mono=300", east=-55, north=-15),
                 caption(7.5, "A battery bank"), blast(7.7, 0.3, "charge=2000 dry=0.2", east=-55, north=10),
                 caption(10.0, "A xenon tank (cold gas, no fire)"), blast(10.2, 0.3, "gas=60", east=-55, north=28),
                 caption(12.5, "Fuel alone, burning in the air"), blast(12.7, 0.8, "fuel=300", east=-55, north=52)],
    },
    # A whole rocket, part by part, through the game's own crash path.
    "rocket": {
        "save": "fx_pad", "launch": "Duna Explorer", "seconds": 22, "camera": {"distance": 300, "pitch": 6, "heading": 25}, "orbit_deg_s": 1.5,
        "cues": [caption(0, "A 51-tonne rocket, blown up part by part"), {"at": 1.0, "do": "destroy", "over_s": 1.6}],
    },
    # A ship flying through smoke stirs it, and its exhaust blows it away.
    "through": {
        "save": "fx_pad", "seconds": 10.5, "look_at": {"east": 0, "north": 0, "up": 70}, "camera": {"distance": 80, "pitch": 0, "heading": 25},
        "cues": [caption(0, "Smoke is pushed about by ships and blown by their engines"), blast(0.3, 0.8, "fuel=140 oxidizer=170", east=0, up=62, surface=False),
                 {"at": 4.0, "do": "rpc", "method": "control", "params": {"throttle": 1, "sas": True, "stage": True}}],
    },
    # Something that hits at a slant throws its fire on ahead, and leaves a mark drawn out the same way.
    "crash": {
        "save": "fx_pad", "seconds": 13, "look_at": {"east": -16, "north": 0, "up": 5, "surface": True}, "camera": {"distance": 46, "pitch": 16, "heading": 15},
        "cues": [caption(0, "A crash at a slant, 45 m/s"), blast(0.6, 0.8, "fuel=90 oxidizer=110 cause=crash speed=45 east=-40 up=-20", east=-14),
                 {"at": 8.5, "do": "camera", "distance": 30, "pitch": 50, "heading": 15}, caption(8.5, "The burn mark it leaves")],
    },
    "night": {
        "save": "fx_pad", "warp": 10800, "seconds": 10, "look_at": PAD, "camera": {"distance": 62, "pitch": 6, "heading": 15},
        "cues": [caption(0, "At night: lit only by its own fire"), blast(0.8, 1.0, FUEL)],
    },
    "duna": {
        "save": "duna_day", "seconds": 9, "look_at": {"east": -30, "north": 0, "up": 6, "surface": True}, "camera": {"distance": 75, "pitch": 7, "heading": 30},
        "cues": [caption(0, "On Duna: thin air, red dust"), blast(0.8, 1.0, FUEL, east=-30)],
    },
    "mun": {
        "save": "mun_target_landed", "seconds": 7, "look_at": {"east": -30, "north": 0, "up": 6, "surface": True}, "camera": {"distance": 75, "pitch": 7, "heading": 30},
        "cues": [caption(0, "On the Mun: no air, so no smoke. The fire flies apart and the dust drops back"), blast(0.8, 1.0, FUEL, east=-30)],
    },
    "orbit": {
        "save": "duna_orbit", "seconds": 5, "look_at": {"east": -16, "north": 0, "up": 2}, "camera": {"distance": 60, "pitch": 10, "heading": 40},
        "cues": [caption(0, "In orbit: it thins away and stays with the wreck"), blast(0.8, 1.0, FUEL, east=-16, up=2, surface=False)],
    },
    # One large blast at a quarter speed.
    "slow": {
        "save": "fx_pad", "seconds": 2.6, "fps": 240, "look_at": PAD, "camera": {"distance": 46, "pitch": 5, "heading": 15},
        "cues": [caption(0, "Quarter speed"), blast(0.2, 1.0, FUEL)],
    },
}
ORDER = ["compare", "close", "slow", "kinds", "rocket", "through", "crash", "night", "duna", "mun", "orbit"]


def rpc(method, params=None, timeout=40):
    return ksp_mcp.rpc(method, params or {}, timeout=timeout)


def set_stage(scene):
    """Load the scene's quicksave and get everything ready, the same way for both passes."""
    rpc("quickload", {"name": scene["save"]}, timeout=45)
    time.sleep(4)
    if scene.get("launch"):
        rpc("launch", {"craft": scene["launch"]}, timeout=45)
        time.sleep(5)
    if scene.get("warp"):
        rpc("warp", {"seconds": scene["warp"], "stop_on_event": False, "timeout_s": 40}, timeout=45)
        time.sleep(3)
    rpc("dev_fx_reload")             # the copy of the mod in this development build, not the one loaded at start
    time.sleep(1.0)


def record(scene, folder, sound):
    params = {"folder": folder, "seconds": scene["seconds"], "fps": scene.get("fps", FPS), "sound": sound, "cues": scene["cues"]}
    for key in ("look_at", "camera", "orbit_deg_s"):
        if key in scene:
            params[key] = scene[key]
    rpc("dev_record", params)
    started = time.time()
    while True:
        time.sleep(1.5)
        status = rpc("dev_record_status")
        if not status.get("recording"):
            if status.get("error"):
                raise SystemExit("recording failed: %s" % status["error"])
            return status
        if time.time() - started > 1200:
            rpc("dev_record_stop")
            raise SystemExit("recording did not finish")


def ffmpeg(*arguments):
    done = subprocess.run(["ffmpeg", "-v", "error", "-y"] + list(arguments), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if done.returncode != 0:
        raise SystemExit("ffmpeg failed: %s" % done.stderr.decode(errors="replace")[-600:])


def render(name):
    scene = SCENES[name]
    folder = os.path.join(WORK, name)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    set_stage(scene)
    status = record(scene, folder, sound=False)
    print("  %s: %d frames at %s" % (name, status["frames"], status["screen"]))
    slow = scene.get("fps", FPS) != FPS
    if not slow:
        set_stage(scene)
        record(scene, folder, sound=True)
    out = os.path.join(WORK, name + ".mp4")
    video = ["-framerate", str(FPS), "-i", os.path.join(folder, "f%05d.jpg")]
    # Slow motion has no sound of its own; give it silence so every scene has the same streams.
    audio = ["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"] if slow else ["-i", os.path.join(folder, "sound.wav")]
    # As large as the game's window was, up to the full size: enlarging what was recorded smaller only makes the file bigger.
    try:
        wide, high = [int(n) for n in status["screen"].split("x")]
    except (KeyError, ValueError):
        wide, high = WIDTH, HEIGHT
    if wide > WIDTH or high > HEIGHT or wide * HEIGHT != high * WIDTH:
        wide, high = WIDTH, HEIGHT
    scale = "scale=%d:%d:force_original_aspect_ratio=decrease,pad=%d:%d:(ow-iw)/2:(oh-ih)/2,fade=t=in:st=0:d=0.25" % (wide, high, wide, high)
    ffmpeg(*(video + audio + ["-vf", scale, "-c:v", "libx264", "-preset", "medium", "-crf", "19", "-pix_fmt", "yuv420p", "-r", str(FPS),
                              "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-shortest", out]))
    return out


def main(argv):
    if len(argv) < 2:
        raise SystemExit(__doc__)
    out, names = argv[1], argv[2:] or ORDER
    os.makedirs(WORK, exist_ok=True)
    clips = [render(name) for name in names]
    if argv[2:]:
        # Only some scenes were asked for: join them with the clips of the others left from an earlier run.
        clips = [os.path.join(WORK, name + ".mp4") for name in ORDER if os.path.exists(os.path.join(WORK, name + ".mp4"))]
    listing = os.path.join(WORK, "clips.txt")
    with open(listing, "w") as f:
        for clip in clips:
            f.write("file '%s'\n" % clip.replace("'", "'\\''"))
    ffmpeg("-f", "concat", "-safe", "0", "-i", listing, "-c", "copy", "-movflags", "+faststart", out)
    print("wrote %s (%.1f MB)" % (out, os.path.getsize(out) / 1e6))


if __name__ == "__main__":
    main(sys.argv)
