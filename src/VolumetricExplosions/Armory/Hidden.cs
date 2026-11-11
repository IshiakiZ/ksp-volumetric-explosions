using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// What of one of BD Armory's objects is hidden while ours is drawn for it: the particle renderers of the emitters that do one
    /// job (its smoke, or its flame). Hidden by the renderers' own switch only (its lights are left as they are): nothing is made
    /// inactive (BD Armory goes by that: a fire's damage, a pool's free objects, whether a smoke screen blinds), and nothing of
    /// BD Armory's own is changed (its emitters' "emit" it sets afresh every frame for some, and the game's emitter code never
    /// touches a renderer's switch, so what is set here stays). And, if the page asks (cutparticles), the particles not worked
    /// out either while they are hidden: their systems' most particles set to none, and what there was of them cleared; the
    /// number put back when they are shown. Everything is put back as it was by Show.
    /// </summary>
    sealed class Hidden
    {
        readonly ParticleSystemRenderer[] renderers;
        readonly ParticleSystem[] systems;
        readonly int[] keys;
        readonly Held[] held;
        readonly bool[] cutHere;
        bool cut;

        // One emitter may be hidden by two at once: a missile's exhaust taken up by the next missile from the same launcher while the
        // first still holds it, or one of BD Armory's pooled effects on a part. So how each was is kept once for all of them, saved by
        // the first to hide it and put back by the last to let it go (each saving it for itself, the second saved it hidden, and
        // whichever let go last left BD Armory's own off for good). By the emitter's object's number: one that is destroyed meanwhile
        // is still let go of.
        sealed class Held { public int hiders, cutters, most; public bool was; }
        static readonly Dictionary<int, Held> holding = new Dictionary<int, Held>();

        /// <summary>Hidden just now.</summary>
        public bool On { get; private set; }
        public int Count => renderers.Length;
        public static readonly Hidden None = new Hidden(new List<KSPParticleEmitter>());

        public Hidden(List<KSPParticleEmitter> emitters)
        {
            renderers = new ParticleSystemRenderer[emitters.Count];
            systems = new ParticleSystem[emitters.Count];
            keys = new int[emitters.Count];
            held = new Held[emitters.Count];
            cutHere = new bool[emitters.Count];
            for (int n = 0; n < emitters.Count; n++)
            {
                KSPParticleEmitter e = emitters[n];
                if (e == null) continue;
                renderers[n] = e.GetComponent<ParticleSystemRenderer>();
                systems[n] = e.GetComponent<ParticleSystem>();
                keys[n] = e.gameObject.GetInstanceID();
            }
        }

        /// <summary>The emitters of an object and of what hangs from it (inactive ones too) that the test picks out, as one thing to hide.</summary>
        public static Hidden Of(GameObject go, Func<KSPParticleEmitter, bool> pick)
        {
            if (go == null) return None;
            var chosen = new List<KSPParticleEmitter>();
            foreach (KSPParticleEmitter e in go.GetComponentsInChildren<KSPParticleEmitter>(true))
                if (e != null && (pick == null || pick(e))) chosen.Add(e);
            return chosen.Count == 0 ? None : new Hidden(chosen);
        }

        /// <summary>The emitters that the test picks out, as one thing to hide.</summary>
        public static Hidden Of(List<KSPParticleEmitter> emitters) => emitters.Count == 0 ? None : new Hidden(emitters);

        /// <summary>Hidden (its particles not worked out either, if cutting), or shown as it was.</summary>
        public void Set(bool hide, bool cutting)
        {
            if (this == None) return;
            if (!hide) { Show(); return; }
            if (!On)
            {
                for (int n = 0; n < renderers.Length; n++)
                {
                    ParticleSystemRenderer r = renderers[n];
                    if (r == null) continue;
                    if (!holding.TryGetValue(keys[n], out Held h)) holding[keys[n]] = h = new Held();
                    if (h.hiders++ == 0) h.was = r.enabled;
                    held[n] = h;
                    r.enabled = false;
                }
                On = true;
            }
            if (cutting && !cut) Cut();
            else if (!cutting && cut) Uncut();
        }

        public void Show()
        {
            if (!On) return;
            if (cut) Uncut();
            for (int n = 0; n < renderers.Length; n++)
            {
                Held h = held[n];
                if (h == null) continue;
                held[n] = null;
                if (--h.hiders > 0) continue;
                holding.Remove(keys[n]);
                if (renderers[n] != null) renderers[n].enabled = h.was;
            }
            On = false;
        }

        void Cut()
        {
            for (int n = 0; n < systems.Length; n++)
            {
                ParticleSystem s = systems[n];
                Held h = held[n];
                if (s == null || h == null) continue;
                ParticleSystem.MainModule main = s.main;
                if (h.cutters++ == 0) { h.most = main.maxParticles; main.maxParticles = 0; }
                cutHere[n] = true;
                s.Clear(false);
            }
            cut = true;
        }

        void Uncut()
        {
            for (int n = 0; n < systems.Length; n++)
            {
                if (!cutHere[n]) continue;
                cutHere[n] = false;
                ParticleSystem s = systems[n];
                Held h = held[n];
                if (--h.cutters > 0 || s == null) continue;
                ParticleSystem.MainModule main = s.main;
                main.maxParticles = h.most;
            }
            cut = false;
        }

        /// <summary>How many of its renderers are hidden just now (for the counts).</summary>
        public int Hiding()
        {
            if (!On) return 0;
            int n = 0;
            foreach (ParticleSystemRenderer r in renderers) if (r != null) n++;
            return n;
        }

        /// <summary>(As the flight ends, once all is shown.) What is still held of emitters destroyed while hidden.</summary>
        public static void Forget() => holding.Clear();
    }
}
