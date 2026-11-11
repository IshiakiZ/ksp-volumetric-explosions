using System.Collections.Generic;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// The sparks of a hit (BD Armory's BulletHitFX, and its PenFX past 30 mm: their emitters named "sparks"), drawn as the explosions mod
    /// draws its own (its Recipe.Sparks): bright streaks thrown off the face struck, white-hot to orange to a dull red, falling and slowing
    /// in the air and bouncing off the ground, gone within a second. A round striking metal throws its sparks mostly along the face it struck, fanned out all round
    /// where it struck, a few straight off it; the more, the faster and the longer the bigger the round. BD Armory draws them as a little
    /// spray of dots for a fifth of a second. All of ours in one particle system, which Unity moves as it moves the explosions mod's
    /// sparks, held to a place on the planet (moved with it as the scene's world is moved), its up the world's up there, so that the
    /// recipe's fall is down; that place moved to the newest hit once the last sparks are gone.
    /// </summary>
    static class HitSparks
    {
        static GameObject holder;
        static ParticleSystem sparks;
        static CelestialBody body;
        static double lat, lon, alt;
        static float lastAt = -100f;

        /// <summary>(For Tests.Said: the hits whose sparks were ours, counted by Volumes.SayHit.)</summary>
        internal static int Thrown;

        /// <summary>
        /// A round of so many millimetres striking a face at a place in the scene (the face's outward way; what it struck going at so many
        /// metres a second over the ground, which its sparks keep as they fly off it), now: its sparks thrown. False where they cannot be
        /// (the explosions mod's materials not made): BD Armory's own are left.
        /// </summary>
        internal static bool Throw(Vector3 at, Vector3 face, Vector3 carried, float caliber, bool big, float now)
        {
            CelestialBody here = FlightGlobals.currentMainBody;
            if (here == null) return false;
            if (sparks == null)
            {
                if (holder == null) holder = new GameObject("Volumetric Armory: sparks");
                sparks = Feed.Sparks(holder.transform);
                if (sparks == null) return false;
                // (never less than two pixels or so across: a spark a few centimetres thick is less than a pixel some tens of metres
                // off, and was lost against the ground in daylight, where a real one is seen as a bright streak)
                var drawn = sparks.GetComponent<ParticleSystemRenderer>();
                drawn.minParticleSize = 0.0025f;
                // (and four times as bright as the explosions mod draws its own, which are larger and seen from nearer: a spark a pixel
                // or two across is drawn from the small copies of its picture, a thin white line in a clear square, in which that line
                // is spread thin, and it came out a third as bright as it is. Filmed at night, 2026-11-10: dull tan threads at 30 m,
                // 100 of 255 over the dark runway, beside lamps burnt out white; a real spark is white hot, among the brightest
                // things a camera sees by night. Seen from near, their middles burn out to white, as they do on film.)
                var bright = new MaterialPropertyBlock();
                bright.SetColor("_TintColor", new Color(2f, 2f, 2f, 0.5f));
                drawn.SetPropertyBlock(bright);
                body = null;
            }
            // (its place on the planet the hit's own when the last sparks are long gone, or another planet's)
            if (body != here || now - lastAt > 3f) Anchor(here, at);
            Hold();
            lastAt = now;
            Transform t = holder.transform;
            Vector3 n = face.sqrMagnitude > 1e-6f ? face.normalized : (Vector3)FlightGlobals.getUpAxis(here, at);
            Vector3 a = Vector3.Cross(n, Mathf.Abs(Vector3.Dot(n, t.up)) < 0.9f ? t.up : t.right).normalized, b = Vector3.Cross(n, a);
            float bore = Mathf.Clamp(caliber, 5f, 200f);
            int count = Mathf.Clamp(Mathf.RoundToInt(4f + 0.4f * bore), 6, 40);
            float fast = 7f + 0.12f * bore, longer = big ? 1.4f : 1f;
            Vector3 from = t.InverseTransformPoint(at + n * 0.05f);
            for (int k = 0; k < count; k++)
            {
                float round = Random.Range(0f, 2f * Mathf.PI);
                Vector3 way = (a * Mathf.Cos(round) + b * Mathf.Sin(round) + n * Random.Range(0.1f, 1.1f)).normalized;
                var e = new ParticleSystem.EmitParams
                {
                    position = from,
                    velocity = t.InverseTransformDirection(way * (fast * Random.Range(0.4f, 1.6f)) + carried),
                    startLifetime = Random.Range(0.15f, 0.8f) * longer,
                    startSize = Random.Range(0.04f, 0.09f) * longer,
                    startColor = new Color32(255, 240, 205, 255),
                };
                sparks.Emit(e, 1);
            }
            return true;
        }

        /// <summary>Once a frame (Volumes.Asked): the sparks' frame held to its place on the planet.</summary>
        internal static void Step()
        {
            if (sparks == null || holder == null || body == null) return;
            if (body != FlightGlobals.currentMainBody) { Forget(); return; }
            Hold();
        }

        /// <summary>(Leaving the scene, or BD Armory's own drawn: every spark put out.)</summary>
        internal static void Forget()
        {
            if (sparks != null) sparks.Clear();
            body = null;
            lastAt = -100f;
            crackling.Clear();
            lookedAt = -100f;
        }

        static void Anchor(CelestialBody here, Vector3 at)
        {
            sparks.Clear();
            body = here;
            here.GetLatLonAlt((Vector3d)at, out lat, out lon, out alt);
            // (how fast they slow, and fall, here: as the explosions mod has it for its own)
            double pressure = here.atmosphere ? FlightGlobals.getStaticPressure((Vector3d)at, here) : 0.0;
            double density = pressure > 0.0 ? FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature((Vector3d)at, here), here) : 0.0;
            float air = Mathf.Clamp((float)(density / 1.225), 0f, 8f);
            float grip = air < 0.004f ? 0f : Mathf.Clamp(Mathf.Sqrt(air), 0.12f, 2.2f);
            ParticleSystem.ForceOverLifetimeModule force = sparks.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.Local;
            force.x = 0f;
            force.y = -(float)FlightGlobals.getGeeForceAtPosition((Vector3d)at, here).magnitude;
            force.z = 0f;
            ParticleSystem.LimitVelocityOverLifetimeModule limit = sparks.limitVelocityOverLifetime;
            limit.enabled = grip > 0f;
            limit.limit = 100000f;
            limit.dampen = 0f;
            limit.drag = 0.35f * grip;
            limit.multiplyDragByParticleSize = false;
        }

        // ---------------------------------------------------------------- an EMP's crackle

        // Where BD Armory's EMP overwhelms a craft it shuts it down for a while, and draws lightning crackling about its root part for
        // five seconds (its Electroshock, which takes itself away when done). That is left as it is, as is the EMP's blue flash; with
        // the lightning, ours: sparks spat from the craft's parts here and there, as wiring shorting under the skin spits them, a few
        // sprays a second over the craft (the more parts, the more), each as a small round's off the part's skin, going on with the
        // craft.

        /// <summary>(For Tests.Said: the sprays spat by craft an EMP shut down.)</summary>
        internal static int Spat;

        static readonly List<Vessel> crackling = new List<Vessel>();
        static float lookedAt = -100f;

        /// <summary>Once a frame (Volumes.Asked): the craft crackling with BD Armory's EMP, sparks spat from one of their parts now and then.</summary>
        internal static void Crackle(float now)
        {
            if (Bda.Shock == null) return;
            if (now - lookedAt > 0.25f || now < lookedAt)
            {
                // (four times a second: the craft with BD Armory's crackle under their root part)
                lookedAt = now;
                crackling.Clear();
                List<Vessel> loaded = FlightGlobals.VesselsLoaded;
                for (int n = 0; loaded != null && n < loaded.Count; n++)
                {
                    Vessel v = loaded[n];
                    if (v == null || v.rootPart == null) continue;
                    Transform root = v.rootPart.transform;
                    for (int k = 0; k < root.childCount; k++)
                        if (root.GetChild(k).GetComponent(Bda.Shock) != null) { crackling.Add(v); break; }
                }
            }
            for (int n = 0; n < crackling.Count; n++)
            {
                Vessel v = crackling[n];
                if (v == null || v.parts == null || v.parts.Count == 0) continue;
                float rate = Mathf.Clamp(2f + 0.25f * v.parts.Count, 3f, 10f);
                if (Random.value > rate * Time.deltaTime) continue;
                Part p = v.parts[Random.Range(0, v.parts.Count)];
                Collider skin = p != null ? p.collider : null;
                if (skin == null || !skin.enabled || (skin is MeshCollider mesh && !mesh.convex)) continue;
                // (a place on its skin: the nearest to a place outside it, any way from its middle)
                Bounds box = skin.bounds;
                Vector3 outside = box.center + Random.onUnitSphere * (box.extents.magnitude + 1f);
                Vector3 on = skin.ClosestPoint(outside), face = outside - on;
                if (face.sqrMagnitude < 1e-6f) continue;
                Vector3 carried = p.Rigidbody != null ? p.Rigidbody.velocity + (Vector3)Krakensbane.GetFrameVelocity() : Vector3.zero;
                if (Throw(on, face, carried, Random.Range(5f, 12f), false, now)) Spat++;
            }
        }

        static readonly Collider[] touched = new Collider[8];

        /// <summary>How fast what is at a place in the scene goes over the ground (m/s): the part nearest it, or nought.</summary>
        internal static Vector3 Carried(Vector3 at)
        {
            int n = Physics.OverlapSphereNonAlloc(at, 0.4f, touched, 1, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < n; k++)
            {
                Part p = touched[k] != null ? touched[k].GetComponentInParent<Part>() : null;
                if (p != null && p.Rigidbody != null) return p.Rigidbody.velocity + (Vector3)Krakensbane.GetFrameVelocity();
            }
            return Vector3.zero;
        }

        static void Hold()
        {
            Vector3d middle = body.GetWorldSurfacePosition(lat, lon, alt);
            Vector3 up = (Vector3)(middle - body.position).normalized;
            holder.transform.SetPositionAndRotation((Vector3)middle, Quaternion.FromToRotation(Vector3.up, up));
        }
    }
}
