using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// Builds the Unity objects the mod needs: the effect the game copies when something explodes (which
    /// holds only the bang), and the particle systems a <see cref="Site"/> draws through.
    /// </summary>
    public static class Recipe
    {
        public static int Layer;                 // the layer the game's own explosions are drawn on
        static Mesh flatQuad;

        /// <param name="strength">0 for the weakest slot (a thud) to 1 for the strongest.</param>
        /// <param name="stockEffect">The effect being replaced; the way its sound is set up is kept.</param>
        public static GameObject Build(Transform shelf, float strength, GameObject stockEffect)
        {
            var root = new GameObject("Volumetric Explosion " + Mathf.RoundToInt(strength * 100));
            root.transform.SetParent(shelf, false);
            root.layer = stockEffect != null ? stockEffect.layer : 0;
            CopySound(stockEffect, root);
            var blast = root.AddComponent<Blast>();
            blast.strength = strength;
            // The stock effects set their own loudness and vary their pitch when they appear; do the same.
            blast.stockVolume = stockEffect != null && stockEffect.GetComponent<VolumeController>() != null;
            return root;
        }

        // ------------------------------------------------------------------ particle systems

        /// <summary>A particle system that only draws: the Site works every particle out itself and hands them over each frame.</summary>
        public static ParticleSystem Drawn(Transform parent, string name, Material material, float drawOrder, int most, bool sheet)
        {
            ParticleSystem system = New(parent, name, material, drawOrder, most);
            ParticleSystem.MainModule main = system.main;
            main.loop = true;                                            // never runs out, so it never stops drawing
            if (sheet)
            {
                // The tile each particle shows is chosen through how far through its "life" it claims to be.
                ParticleSystem.TextureSheetAnimationModule tiles = system.textureSheetAnimation;
                tiles.enabled = true;
                tiles.mode = ParticleSystemAnimationMode.Grid;
                tiles.numTilesX = Assets.Tiles;
                tiles.numTilesY = Assets.Tiles;
                tiles.animation = ParticleSystemAnimationType.WholeSheet;
                tiles.cycleCount = 1;
                tiles.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            }
            system.Play(false);
            return system;
        }

        /// <summary>Bright streaks thrown clear, falling back and bouncing: Unity moves these itself.</summary>
        public static ParticleSystem Sparks(Transform parent, float gravity, float air, bool ground)
        {
            ParticleSystem system = New(parent, "Sparks", Assets.Spark, -30f, 1500);
            Fall(system, gravity, air * 0.35f);
            Fade(system, new[] { new Color(1f, 0.97f, 0.85f), new Color(1f, 0.72f, 0.3f), new Color(0.9f, 0.25f, 0.05f) }, new[] { 1f, 0.95f, 0f });
            var streaks = system.GetComponent<ParticleSystemRenderer>();
            streaks.renderMode = ParticleSystemRenderMode.Stretch;
            streaks.velocityScale = 0.035f;
            streaks.lengthScale = 1.6f;
            if (ground) Bounce(system, 0.35f);
            system.Play(false);
            return system;
        }

        /// <summary>Bits of the thing that blew up, tumbling.</summary>
        public static ParticleSystem Shards(Transform parent, float gravity, float air, bool ground)
        {
            ParticleSystem system = New(parent, "Debris", Assets.Shard, -20f, 400);
            Fall(system, gravity, air * 0.08f);
            Fade(system, new[] { Color.white, Color.white, Color.white }, new[] { 1f, 1f, 0f }, 0.85f);
            ParticleSystem.RotationOverLifetimeModule spin = system.rotationOverLifetime;
            spin.enabled = true;
            spin.z = new ParticleSystem.MinMaxCurve(-9f, 9f);
            if (ground) Bounce(system, 0.25f);
            system.Play(false);
            return system;
        }

        /// <summary>The blast wave: one ring that grows fast and fades. Flat along the ground, or facing the camera in the open air.</summary>
        public static ParticleSystem Ring(Transform parent, bool flat)
        {
            ParticleSystem system = New(parent, flat ? "Blast wave (ground)" : "Blast wave", Assets.Ring, -10f, 8);
            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.04f, 0f, 2.6f), new Keyframe(1f, 1f, 0.35f, 0f)));
            Fade(system, new[] { new Color(1f, 0.95f, 0.88f), new Color(1f, 0.93f, 0.85f), new Color(1f, 0.9f, 0.8f) }, new[] { 0.42f, 0.25f, 0f });
            if (flat)
            {
                var ring = system.GetComponent<ParticleSystemRenderer>();
                ring.renderMode = ParticleSystemRenderMode.Mesh;
                ring.mesh = FlatQuad();
                ring.alignment = ParticleSystemRenderSpace.Local;
            }
            system.Play(false);
            return system;
        }

        static ParticleSystem New(Transform parent, string name, Material material, float drawOrder, int most)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            holder.layer = Layer;
            var system = holder.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = system.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;  // the Site's frame is fixed to the ground, or flies with the wreck
            main.startSpeed = 0f;
            main.gravityModifier = 0f;                                    // the game runs its own gravity; it is added as a force
            main.maxParticles = most;
            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;
            var renderer = holder.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.sortingFudge = drawOrder;                            // higher is drawn earlier, so it ends up behind
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.maxParticleSize = 4f;                                // let a puff fill the screen when the camera is inside it
            renderer.minParticleSize = 0f;
            return system;
        }

        static void Fall(ParticleSystem system, float gravity, float drag)
        {
            ParticleSystem.ForceOverLifetimeModule force = system.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.Local;
            force.x = 0f;
            force.y = -gravity;
            force.z = 0f;
            if (drag <= 0.001f) return;
            ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 100000f;
            limit.dampen = 0f;
            limit.drag = drag;
            limit.multiplyDragByParticleSize = false;
            limit.multiplyDragByParticleVelocity = true;
        }

        static void Bounce(ParticleSystem system, float bounce)
        {
            ParticleSystem.CollisionModule hit = system.collision;
            hit.enabled = true;
            hit.type = ParticleSystemCollisionType.World;
            hit.mode = ParticleSystemCollisionMode.Collision3D;
            hit.collidesWith = 1 << 15;                                   // the ground and the buildings
            hit.quality = ParticleSystemCollisionQuality.Medium;
            hit.bounce = bounce;
            hit.dampen = 0.45f;
            hit.lifetimeLoss = 0.25f;
            hit.radiusScale = 0.3f;
            hit.enableDynamicColliders = false;
            hit.maxCollisionShapes = 64;
        }

        static void Fade(ParticleSystem system, Color[] colours, float[] alphas, float middle = 0.4f)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(colours[0], 0f), new GradientColorKey(colours[1], middle), new GradientColorKey(colours[2], 1f) },
                new[] { new GradientAlphaKey(alphas[0], 0f), new GradientAlphaKey(alphas[1], middle), new GradientAlphaKey(alphas[2], 1f) });
            ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        /// <summary>A square lying flat, for the blast wave along the ground.</summary>
        public static Mesh FlatQuad()
        {
            if (flatQuad != null) return flatQuad;
            flatQuad = new Mesh { name = "VolumetricExplosions flat quad" };
            flatQuad.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
            flatQuad.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            flatQuad.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };      // both faces
            flatQuad.RecalculateNormals();
            flatQuad.RecalculateBounds();
            return flatQuad;
        }

        /// <summary>The game plays the bang on this object; set the speaker up the way the stock effect has it.</summary>
        static void CopySound(GameObject stockEffect, GameObject root)
        {
            AudioSource from = stockEffect != null ? stockEffect.GetComponent<AudioSource>() : null;
            var to = root.AddComponent<AudioSource>();
            to.playOnAwake = false;
            if (from == null)
            {
                to.spatialBlend = 1f;
                to.rolloffMode = AudioRolloffMode.Linear;
                to.minDistance = 10f;
                to.maxDistance = 2500f;
                return;
            }
            // Most of the bang is not the sound the game plays on top (that is only the crack): the stock
            // effect carries the long rumble as its own clip and starts it by itself.
            to.clip = from.clip;
            to.playOnAwake = from.playOnAwake;
            to.loop = from.loop;
            to.ignoreListenerVolume = from.ignoreListenerVolume;
            to.reverbZoneMix = from.reverbZoneMix;
            to.panStereo = from.panStereo;
            to.velocityUpdateMode = from.velocityUpdateMode;
            to.spatialBlend = from.spatialBlend;
            to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance;
            to.maxDistance = from.maxDistance;
            to.dopplerLevel = from.dopplerLevel;
            to.spread = from.spread;
            to.priority = from.priority;
            to.volume = from.volume;
            to.pitch = from.pitch;
            to.outputAudioMixerGroup = from.outputAudioMixerGroup;
            if (from.rolloffMode == AudioRolloffMode.Custom)
                to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }
    }
}
