using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>One of the meshes a part was drawn with, kept from the moment before the part was destroyed.</summary>
    public sealed class Shell
    {
        public Mesh mesh;
        public Material material;
        public Matrix4x4 toWorld;        // where it stood in the scene in that frame
        public Matrix4x4 local;          // the same, in the frame of the Site the explosion went to

        /// <summary>The meshes of a part's own model (not of the parts attached to it), biggest first, a few at most.</summary>
        public static List<Shell> Of(Part p)
        {
            List<MeshFilter> filters = p.FindModelComponents<MeshFilter>();
            if (filters == null || filters.Count == 0) return null;
            List<Shell> shells = null;
            foreach (MeshFilter f in filters)
            {
                if (f == null) continue;
                Mesh mesh = f.sharedMesh;
                var renderer = f.GetComponent<MeshRenderer>();
                if (mesh == null || !mesh.isReadable || mesh.vertexCount < 12 || renderer == null || !renderer.enabled || renderer.sharedMaterial == null || !f.gameObject.activeInHierarchy) continue;
                if (shells == null) shells = new List<Shell>();
                shells.Add(new Shell { mesh = mesh, material = renderer.sharedMaterial, toWorld = f.transform.localToWorldMatrix });
            }
            if (shells == null) return null;
            shells.Sort((a, b) => b.mesh.vertexCount.CompareTo(a.mesh.vertexCount));
            if (shells.Count > 4) shells.RemoveRange(4, shells.Count - 4);
            return shells;
        }
    }

    /// <summary>A mesh's data read out once and kept, since a rocket is mostly many copies of a few parts.</summary>
    sealed class Reading
    {
        public Vector3[] points, normals;
        public Vector2[] uv;
        public Vector4[] tangents;
        public int[] triangles, map;
        public int stamp;

        static readonly Dictionary<int, Reading> kept = new Dictionary<int, Reading>();

        public static Reading Of(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (kept.TryGetValue(id, out Reading r)) return r;
            if (kept.Count > 10) kept.Clear();
            r = new Reading { points = mesh.vertices, normals = mesh.normals, uv = mesh.uv, tangents = mesh.tangents, triangles = mesh.triangles };
            r.map = new int[r.points.Length];
            if (r.normals.Length != r.points.Length) r.normals = null;
            if (r.uv.Length != r.points.Length) r.uv = null;
            if (r.tangents.Length != r.points.Length) r.tangents = null;
            kept[id] = r;
            return r;
        }

        public static void Forget() => kept.Clear();
    }

    /// <summary>
    /// The pieces of a ship thrown out of an explosion: patches of the destroyed part's own skin, torn off
    /// where its triangles meet, tumbling, falling, bouncing, some of them burning or glowing hot.
    /// How many there are, how big and how fast depends on what happened to the part; often there are none.
    /// They are drawn but are not solid: nothing in the game can be hit by one.
    /// </summary>
    public sealed partial class Site
    {
        sealed class Piece
        {
            public GameObject holder;
            public Transform tf;
            public Mesh mesh;
            public Material material;
            public Vector3 x, v, spin;       // position, velocity, and turning (radians a second about each axis)
            public Quaternion turn;
            public Vector3 size;
            public float reach;              // how far its edge is from its middle
            public float slows;              // how much the air holds it back
            public float heat, burns, age, life, due, trail;
            public bool resting, white, glows;
            public byte tint;
        }

        struct Job { public Death part; public Plan plan; public int count; }

        readonly List<Piece> pieces = new List<Piece>();
        readonly Queue<Job> toBreak = new Queue<Job>();
        static int piecesAlive;
        const int MostPieces = 70;

        /// <summary>Decide how many pieces each destroyed part leaves, and queue the breaking up (it is spread over a few frames).</summary>
        void Wreck(Plan pl)
        {
            if (!Settings.Debris || pl.wreck == null) return;
            foreach (Death d in pl.wreck)
            {
                if (d.shells == null) continue;
                int n = HowMany(pl, d);
                if (n > 0) toBreak.Enqueue(new Job { part = d, plan = pl, count = n });
            }
        }

        /// <summary>What a part leaves behind depends on what happened to it.</summary>
        static int HowMany(Plan pl, Death d)
        {
            bool carried = d.fuel + d.oxidizer + d.solid + d.mono > 5.0;       // it had propellant in it
            float bigger = 1f + Mathf.Min(3f, d.size * 0.8f);                  // large parts leave more
            float n;
            if (d.cause == Cause.Overheat || d.cause == Cause.Exhaust)
            {
                // Burned through: mostly it is simply gone.
                if (Random.value < 0.7f) return 0;
                n = Random.Range(1f, 2.4f);
            }
            else if (pl.radius > 0.3f && carried)
            {
                // A tank that burst: torn into panels and flung. A fire far bigger than the part can swallow it whole.
                if (pl.radius > d.size * 7f && Random.value < 0.3f) return 0;
                n = Random.Range(2f, 4.5f) * bigger;
            }
            else if (d.cause == Cause.Crash || d.cause == Cause.Collision)
            {
                if (d.impact < 15f) { if (Random.value < 0.4f) return 0; n = Random.Range(1f, 2.5f); }      // crumpled where it lay
                else if (d.impact < 60f) n = Random.Range(1.5f, 3f) * bigger;
                else n = Random.Range(2.5f, 4.5f) * bigger;                                                // smashed and sprayed on ahead
            }
            else if (d.cause == Cause.GForce || d.cause == Cause.Pressure)
            {
                if (Random.value < 0.15f) return 0;
                n = Random.Range(1f, 3.2f);
            }
            else
            {
                if (pl.radius > 0.3f && Random.value < 0.2f) return 0;
                n = Random.Range(1f, 3f) * Mathf.Sqrt(bigger);
            }
            if (d.size < 0.35f || d.dry < 0.02f) { if (Random.value < 0.5f) return 0; n *= 0.5f; }           // small things mostly just vanish
            return Mathf.Clamp(Mathf.RoundToInt(n * Mathf.Min(1.5f, Settings.Quality)), 0, 10);
        }

        /// <summary>Break up a part or two a frame, so that a whole rocket coming apart does not stall the game.</summary>
        void BreakSome()
        {
            float until = Time.realtimeSinceStartup + 0.002f;
            while (toBreak.Count > 0 && Time.realtimeSinceStartup < until)
            {
                Job job = toBreak.Dequeue();
                try { Break(job.plan, job.part, job.count); }
                catch (Exception ex) { if (Settings.Log) Addon.Log("could not break a part up: " + ex.Message); }
            }
        }

        void Break(Plan pl, Death d, int count)
        {
            bool carried = d.fuel + d.oxidizer + d.solid + d.mono > 5.0;
            bool fiery = pl.radius > 0.3f && !pl.underwater;
            bool smashed = (d.cause == Cause.Crash || d.cause == Cause.Collision) && d.impact >= 60f;
            bool gentle = d.cause == Cause.GForce || d.cause == Cause.Pressure || ((d.cause == Cause.Crash || d.cause == Cause.Collision) && d.impact < 15f && !fiery);
            bool burnedUp = d.cause == Cause.Overheat || d.cause == Cause.Exhaust;
            Vector3 going = d.going, centre = pl.at;
            float floor = hasGround ? GroundAt(d.at.x, d.at.z) : float.MinValue;
            bool onGround = hasGround && d.at.y - floor < d.size + 2f;

            for (int k = 0; k < count && piecesAlive < MostPieces; k++)
            {
                Shell shell = d.shells[Random.Range(0, Mathf.Min(d.shells.Count, k == 0 ? 1 : d.shells.Count))];      // the first piece is always of the main body
                // Violent ends make small pieces; gentle ones leave big ones.
                float share = smashed || (fiery && carried) ? Random.Range(0.18f, 0.42f) : burnedUp ? Random.Range(0.12f, 0.3f) : Random.Range(0.3f, 0.62f);
                Piece piece = Tear(shell, share);
                if (piece == null) continue;

                Vector3 away = piece.x - centre;
                away = away.sqrMagnitude > 0.01f ? away.normalized : Random.onUnitSphere;
                away = (away + Random.onUnitSphere * 0.6f).normalized;
                if (onGround && away.y < 0.1f) away.y = Mathf.Abs(away.y) + 0.2f;
                float thrown;
                if (burnedUp) thrown = Random.Range(1f, 6f);
                else if (fiery && carried) thrown = (4f + 3.5f * Mathf.Sqrt(pl.radius)) * Random.Range(0.4f, 1.6f);
                else if (fiery) thrown = (2f + 2f * Mathf.Sqrt(pl.radius)) * Random.Range(0.3f, 1.3f);       // blown off by somebody else's fire
                else if (gentle) thrown = Random.Range(0.3f, 2.5f);
                else thrown = Random.Range(1.5f, 5f) + d.impact * Random.Range(0.03f, 0.12f);
                Vector3 carriedOn = going * Random.Range(0.55f, 1f);
                if (onGround && (d.cause == Cause.Crash || d.cause == Cause.Collision) && carriedOn.y < 0f)
                {
                    // It hit the ground: what is left skips on along it and up, with much of the speed gone.
                    carriedOn.y = -carriedOn.y * Random.Range(0.15f, 0.5f);
                    carriedOn.x *= Random.Range(0.35f, 0.8f);
                    carriedOn.z *= Random.Range(0.35f, 0.8f);
                }
                piece.v = carriedOn + away * thrown;
                float tumble = gentle ? Random.Range(0.3f, 2f) : Random.Range(2f, 9f) + thrown * 0.15f;
                piece.spin = Random.onUnitSphere * tumble;
                piece.slows = vacuum ? 0f : Random.Range(0.02f, 0.07f) * Mathf.Min(1.5f, air) / Mathf.Max(0.3f, piece.reach);
                piece.life = Random.Range(28f, 50f);
                // Burning, or just hot from the fire it came out of.
                if (fiery && (carried || Random.value < 0.4f) && !vacuum && Random.value < 0.55f)
                {
                    piece.burns = Random.Range(1.2f, 5f) * (d.solid > 5.0 ? 1.8f : 1f);
                    piece.white = d.solid > d.fuel;
                    piece.tint = (byte)pl.tint;
                    piece.trail = Mathf.Clamp(piece.reach * 0.35f, 0.15f, 0.55f);
                }
                piece.heat = burnedUp ? 1f : fiery ? Random.Range(0.1f, 0.6f) : smashed ? Random.Range(0f, 0.2f) : 0f;
                Blacken(piece, burnedUp ? 0.3f : fiery ? Random.Range(0.25f, 0.7f) : Random.Range(0.75f, 1f));
                pieces.Add(piece);
                piecesAlive++;
            }
        }

        /// <summary>Cut a patch out of a mesh: the triangles within reach of one picked at random, made into an object of their own.</summary>
        Piece Tear(Shell shell, float share)
        {
            Reading r = Reading.Of(shell.mesh);
            int triangles = r.triangles.Length / 3;
            if (triangles < 4) return null;
            Matrix4x4 m = shell.local;
            var size = new Vector3(((Vector3)m.GetColumn(0)).magnitude, ((Vector3)m.GetColumn(1)).magnitude, ((Vector3)m.GetColumn(2)).magnitude);
            if (size.x < 1e-4f || size.y < 1e-4f || size.z < 1e-4f) return null;
            Bounds box = shell.mesh.bounds;
            // How far the patch reaches, in the mesh's own units: a share of its longest side.
            float within = Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z)) * share;
            int seed = Random.Range(0, triangles) * 3;
            Vector3 middle = (r.points[r.triangles[seed]] + r.points[r.triangles[seed + 1]] + r.points[r.triangles[seed + 2]]) / 3f;

            var picked = new List<int>(256);
            float within2 = within * within;
            for (int n = 0; n < r.triangles.Length; n += 3)
            {
                Vector3 c = (r.points[r.triangles[n]] + r.points[r.triangles[n + 1]] + r.points[r.triangles[n + 2]]) / 3f;
                if ((c - middle).sqrMagnitude > within2) continue;
                picked.Add(n);
                if (picked.Count >= 500) break;
            }
            if (picked.Count < 2) return null;

            // Give the patch its own list of corners, and a second face on the back so that the inside shows too.
            r.stamp++;
            var corners = new List<int>(picked.Count * 2);
            for (int n = 0; n < picked.Count; n++)
                for (int k = 0; k < 3; k++)
                {
                    int v = r.triangles[picked[n] + k];
                    if (r.map[v] >> 12 != r.stamp || corners.Count <= (r.map[v] & 4095) || corners[r.map[v] & 4095] != v)
                    {
                        if (corners.Count >= 4095) continue;
                        r.map[v] = (r.stamp << 12) | corners.Count;
                        corners.Add(v);
                    }
                }
            int count = corners.Count;
            var points = new Vector3[count * 2];
            var normals = new Vector3[count * 2];
            var uv = new Vector2[count * 2];
            Vector4[] tangents = r.tangents != null ? new Vector4[count * 2] : null;
            Vector3 centre = Vector3.zero;
            for (int n = 0; n < count; n++) centre += r.points[corners[n]];
            centre /= count;
            float reach = 0f;
            for (int n = 0; n < count; n++)
            {
                int v = corners[n];
                Vector3 at = r.points[v] - centre;
                points[n] = points[n + count] = at;
                reach = Mathf.Max(reach, Vector3.Scale(at, size).magnitude);
                Vector3 normal = r.normals != null ? r.normals[v] : Vector3.up;
                normals[n] = normal;
                normals[n + count] = -normal;
                if (r.uv != null) uv[n] = uv[n + count] = r.uv[v];
                if (tangents != null) tangents[n] = tangents[n + count] = r.tangents[v];
            }
            var faces = new List<int>(picked.Count * 6);
            for (int n = 0; n < picked.Count; n++)
            {
                int a = Corner(r, corners, r.triangles[picked[n]]), b = Corner(r, corners, r.triangles[picked[n] + 1]), c = Corner(r, corners, r.triangles[picked[n] + 2]);
                if (a < 0 || b < 0 || c < 0) continue;
                faces.Add(a); faces.Add(b); faces.Add(c);
                faces.Add(a + count); faces.Add(c + count); faces.Add(b + count);
            }
            if (faces.Count < 6) return null;

            var mesh = new Mesh { name = "VolumetricExplosions piece" };
            mesh.vertices = points;
            mesh.normals = normals;
            mesh.uv = uv;
            if (tangents != null) mesh.tangents = tangents;
            mesh.SetTriangles(faces, 0);
            mesh.RecalculateBounds();

            var piece = new Piece { mesh = mesh, size = size, reach = Mathf.Max(0.1f, reach) };
            piece.holder = new GameObject("VolumetricExplosions piece") { layer = 0 };
            piece.tf = piece.holder.transform;
            piece.tf.SetParent(t, false);
            piece.x = m.MultiplyPoint3x4(centre);
            piece.turn = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
            piece.tf.localPosition = piece.x;
            piece.tf.localRotation = piece.turn;
            piece.tf.localScale = size;
            piece.holder.AddComponent<MeshFilter>().sharedMesh = mesh;
            piece.material = new Material(shell.material);
            piece.holder.AddComponent<MeshRenderer>().sharedMaterial = piece.material;
            return piece;
        }

        static int Corner(Reading r, List<int> corners, int vertex)
        {
            int at = r.map[vertex];
            if (at >> 12 != r.stamp) return -1;
            at &= 4095;
            return at < corners.Count && corners[at] == vertex ? at : -1;
        }

        /// <summary>Blacken a piece (1 is untouched, 0 black) and set how hot it glows.</summary>
        static void Blacken(Piece piece, float clean)
        {
            Material material = piece.material;
            if (material.HasProperty("_Color"))
            {
                Color c = material.GetColor("_Color");
                material.SetColor("_Color", new Color(c.r * clean, c.g * clean * 0.97f, c.b * clean * 0.93f, c.a));
            }
            piece.glows = material.HasProperty("_TemperatureColor");
            if (piece.glows) material.SetColor("_TemperatureColor", new Color(1f, 0.32f, 0.05f, piece.heat));
        }

        void Pieces(float dt)
        {
            BreakSome();
            float every = 1f / (22f * Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f));
            float lasts = Mathf.Max(0.3f, Settings.Smoke);
            for (int n = pieces.Count - 1; n >= 0; n--)
            {
                Piece piece = pieces[n];
                piece.age += dt;
                float left = piece.life - piece.age;
                if (left <= 0f || piece.x.sqrMagnitude > 3000f * 3000f)
                {
                    Remove(piece);
                    pieces.RemoveAt(n);
                    continue;
                }
                if (!piece.resting)
                {
                    piece.v.y -= gravity * dt;
                    float speed = piece.v.magnitude;
                    piece.v *= 1f / (1f + piece.slows * speed * dt);
                    piece.x += piece.v * dt;
                    float turned = piece.spin.magnitude;
                    if (turned > 1e-3f) piece.turn = Quaternion.AngleAxis(turned * dt * Mathf.Rad2Deg, piece.spin / turned) * piece.turn;
                    if (hasGround)
                    {
                        float floor = GroundAt(piece.x.x, piece.x.z) + piece.reach * 0.25f;
                        if (piece.x.y < floor)
                        {
                            piece.x.y = floor;
                            if (piece.v.y < -2.5f || new Vector2(piece.v.x, piece.v.z).sqrMagnitude > 9f)
                            {
                                // It bounces, losing most of its speed, and tumbles another way.
                                piece.v.y = -piece.v.y * Random.Range(0.15f, 0.4f);
                                piece.v.x *= 0.55f; piece.v.z *= 0.55f;
                                piece.spin = piece.spin * 0.4f + Random.onUnitSphere * Mathf.Min(6f, speed * 0.3f);
                            }
                            else
                            {
                                piece.resting = true;
                                piece.v = Vector3.zero;
                                piece.life = Mathf.Min(piece.life, piece.age + Random.Range(18f, 35f));
                            }
                        }
                    }
                    piece.tf.localPosition = piece.x;
                    piece.tf.localRotation = piece.turn;
                }
                if (left < 2f) piece.tf.localScale = piece.size * (left / 2f);       // it shrinks away at the end
                if (piece.glows && piece.heat > 0f)
                {
                    piece.heat = Mathf.Max(0f, piece.heat - dt * (vacuum ? 0.06f : 0.14f));
                    piece.material.SetColor("_TemperatureColor", new Color(1f, 0.32f, 0.05f, piece.heat));
                }
                if (piece.burns > 0f)
                {
                    piece.burns -= dt;
                    piece.due -= dt;
                    while (piece.due <= 0f)
                    {
                        piece.due += piece.resting ? every * 2.5f : every;
                        if (!Trail(piece.x.x, piece.x.y, piece.x.z, piece.v.x, piece.v.y, piece.v.z, piece.trail, piece.tint, piece.white, lasts)) break;
                    }
                }
            }
        }

        void Remove(Piece piece)
        {
            if (piece.holder != null) UnityEngine.Object.Destroy(piece.holder);
            if (piece.mesh != null) UnityEngine.Object.Destroy(piece.mesh);
            if (piece.material != null) UnityEngine.Object.Destroy(piece.material);
            piecesAlive = Mathf.Max(0, piecesAlive - 1);
        }

        void RemovePieces()
        {
            foreach (Piece piece in pieces) Remove(piece);
            pieces.Clear();
            toBreak.Clear();
        }
    }
}
