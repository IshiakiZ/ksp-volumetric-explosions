// Bakes the small textures of the Volumetric Explosions mod.
//
// The mod draws an explosion as thousands of small soft particles, each shaded by the game as part of a
// volume (see Site.cs in the mod). The particles themselves are only soft irregular blobs with no picture
// of fire or smoke in them; this program makes those blobs, and the few other textures: a glow, a spark,
// a blast ring, a shard of metal and a scorch mark. Colours are stored premultiplied (colour already
// multiplied by opacity), which lets one particle both glow like fire and block the view like smoke.
//
//   dotnet run -c Release --project tools/fxbake -- <output folder> [--preview]

using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

static class Program
{
    public const int Tile = 128, Columns = 4, Rows = 4;

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: fxbake <output folder> [--preview]"); return 2; }
        string folder = args[0];
        bool preview = args.Length > 1 && args[1] == "--preview";
        Directory.CreateDirectory(folder);

        int width = Tile * Columns, height = Tile * Rows;
        var atlas = new byte[width * height * 4];
        Parallel.For(0, Columns * Rows, n => Splat.Render(atlas, width, n % Columns, n / Columns, n));
        Png.Write(Path.Combine(folder, "puff.png"), atlas, width, height);
        Png.Write(Path.Combine(folder, "glow.png"), Simple.Glow(256), 256, 256);
        Png.Write(Path.Combine(folder, "spark.png"), Simple.Spark(128), 128, 128);
        Png.Write(Path.Combine(folder, "ring.png"), Simple.Ring(512), 512, 512);
        Png.Write(Path.Combine(folder, "shard.png"), Simple.Shard(128), 128, 128);
        Png.Write(Path.Combine(folder, "scorch.png"), Simple.Scorch(512), 512, 512);
        Png.Write(Path.Combine(folder, "embers.png"), Simple.Embers(256), 256, 256);
        File.WriteAllBytes(Path.Combine(folder, "detail.bin"), Detail.Block(96, out float steepest));
        Console.WriteLine("puff.png " + width + "x" + height + ", glow.png, spark.png, ring.png, shard.png, scorch.png, detail.bin (96 cells each way; mean " + Detail.Mean + ", spread " + Detail.Spread + ", steepest slope " + steepest.ToString("F2") + " per repeat: the shader's Steepest)");

        if (preview)
        {
            // The sheet as it looks over a night sky and over a day sky, to judge it without starting the game.
            Png.Write(Path.Combine(folder, "preview_dark.png"), Simple.Over(atlas, width, height, 10, 12, 20), width, height);
            Png.Write(Path.Combine(folder, "preview_sky.png"), Simple.Over(atlas, width, height, 120, 160, 215), width, height);
        }
        return 0;
    }
}

/// <summary>
/// The particle sprites, sixteen to a sheet: tiles 0 to 7 are lumpy blobs (smoke and fire), 8 to 11 thin
/// stringy wisps (vapour, trails), 12 to 14 clusters of specks (spray, thrown dirt), 15 a plain soft ball.
/// </summary>
static class Splat
{
    public static void Render(byte[] atlas, int atlasW, int tileX, int tileYFromTop, int n)
    {
        int size = Program.Tile, seed = 9100 + n * 613;
        int x0 = tileX * size, y0 = (Program.Rows - 1 - tileYFromTop) * size;      // rows are stored bottom-up
        float ox = n * 3.71f, oy = n * 1.93f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float edge = Volume.Smooth(Volume.Saturate((1f - r) / 0.14f));              // nothing reaches the border
                float a;
                if (n < 8)
                {
                    // A soft ball, dense in the middle and thinning outward, its reach pushed in and out by
                    // rounded lumps so that no two overlap the same way.
                    float lumps = Noise.Billow(u * 1.15f + ox, v * 1.15f + oy, 0.37f * n, 3, seed);
                    float fine = Noise.Billow(u * 2.9f + oy, v * 2.9f + ox, 1.7f + n, 2, seed + 5);
                    float reach = 0.40f + 0.44f * lumps + 0.10f * fine;
                    float q = r / reach;
                    a = n < 4 ? MathF.Exp(-1.7f * MathF.Pow(q, 2.2f)) : MathF.Exp(-1.25f * MathF.Pow(q, 3.4f));   // the second four are firmer
                    a *= 0.80f + 0.20f * Noise.Value(u * 2.4f + ox, v * 2.4f - oy, 3.3f, seed + 9);
                }
                else if (n < 12)
                {
                    // Thin smoke pulled into strings.
                    float wx = u + 0.35f * (Noise.Value(u * 1.6f + ox, v * 1.6f, 5.5f, seed) - 0.5f), wy = v + 0.35f * (Noise.Value(u * 1.6f, v * 1.6f + oy, 8.5f, seed) - 0.5f);
                    float strings = 1f - MathF.Abs(2f * Noise.Value(wx * 2.3f + ox, wy * 2.3f + oy, 2.2f, seed + 3) - 1f);
                    float body = Volume.Smooth(Volume.Saturate((1f - r * 1.05f) / 0.7f));
                    a = body * (0.10f + 0.9f * MathF.Pow(strings, 2.6f)) * 0.8f;
                }
                else if (n < 15)
                {
                    // Specks, thick in the middle and thinning outward.
                    float specks = Volume.Saturate((Noise.Value(u * 13f + ox, v * 13f + oy, 0.5f, seed) - 0.66f) / 0.07f);
                    float finer = Volume.Saturate((Noise.Value(u * 25f + oy, v * 25f + ox, 4.5f, seed + 1) - 0.68f) / 0.07f);
                    float body = MathF.Exp(-2.6f * r * r);
                    a = body * Volume.Saturate(specks + 0.7f * finer) + body * body * 0.12f;
                }
                else a = MathF.Exp(-r * r * 4.2f);
                a = Volume.Saturate(a * edge);
                int at = ((y0 + y) * atlasW + x0 + x) * 4;
                atlas[at] = atlas[at + 1] = atlas[at + 2] = atlas[at + 3] = Volume.ToByte(a);    // white, premultiplied
            }
    }
}

/// <summary>
/// The fine detail of the smoke volume: a cube of noise, 'side' cells each way, that repeats without a
/// seam. Four bytes a cell. The first is how much the smoke is gathered there: rounded lumps (the cells of
/// a random lattice, turned inside out) with smaller lumps on them and smaller ones again, which is what
/// makes smoke look like puffs rather than fog. The other three are the slope of the bigger lumps, each
/// way: the shader tells from it which side of a puff faces the sun without having to read the cube a second time.
/// </summary>
static class Detail
{
    public const float Mean = 0.45f, Spread = 0.2f;          // what the first byte is scaled to: its mean and how widely it varies about it

    public static byte[] Block(int side, out float steepest)
    {
        int count = side * side * side;
        var lumps = new float[count];
        var broad = new float[count];                            // the big lumps alone: the slope is taken from these, so that it is whole puffs that get a lit side and a shaded one, not every pimple
        Vector3[][] points = { Points(4, 11), Points(9, 12), Points(20, 13) };
        Parallel.For(0, side, z =>
        {
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    float u = (x + 0.5f) / side, v = (y + 0.5f) / side, w = (z + 0.5f) / side;
                    // The lattice the big lumps sit on is bent out of true by smooth noise. Left straight, the troughs between
                    // them are flat planes, and smoke cut along them comes apart in straight-edged pieces, like torn card.
                    float bu = 0.17f * (Tiled(u, v, w, 3, 71) - 0.5f) + 0.06f * (Tiled(u, v, w, 7, 72) - 0.5f);
                    float bv = 0.17f * (Tiled(u, v, w, 3, 73) - 0.5f) + 0.06f * (Tiled(u, v, w, 7, 74) - 0.5f);
                    float bw = 0.17f * (Tiled(u, v, w, 3, 75) - 0.5f) + 0.06f * (Tiled(u, v, w, 7, 76) - 0.5f);
                    // (the smaller lumps sit where smooth noise has pushed them a little further, so that they do not line up with the big ones)
                    float push = 0.06f * (Tiled(u, v, w, 6, 42) - 0.5f);
                    float big = Cells(points[0], 4, u + bu, v + bv, w + bw), middling = Cells(points[1], 9, u + 0.6f * bu + push, v + 0.6f * bv - push, w + 0.6f * bw + push),
                          little = Cells(points[2], 20, u + 0.3f * bu - push, v + 0.3f * bv + push, w + 0.3f * bw - push);
                    lumps[x + (y + z * side) * side] = 0.52f * big + 0.32f * middling + 0.16f * little;
                    broad[x + (y + z * side) * side] = 0.62f * big + 0.38f * middling;
                }
        });
        double sum = 0, squares = 0;
        foreach (float f in lumps) { sum += f; squares += f * f; }
        float mean = (float)(sum / count), spread = (float)Math.Sqrt(squares / count - mean * mean), scale = Spread / spread;
        for (int n = 0; n < count; n++) { lumps[n] = Volume.Saturate(Mean + (lumps[n] - mean) * scale); broad[n] *= scale; }

        // The slope, as the change over one whole repeat of the pattern; kept as a share of the steepest there is (all but a few stray cells).
        var slopes = new float[count * 3];
        Parallel.For(0, side, z =>
        {
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int at = x + (y + z * side) * side;
                    slopes[at * 3] = (broad[Wrap(x + 1, side) + (y + z * side) * side] - broad[Wrap(x - 1, side) + (y + z * side) * side]) * 0.5f * side;
                    slopes[at * 3 + 1] = (broad[x + (Wrap(y + 1, side) + z * side) * side] - broad[x + (Wrap(y - 1, side) + z * side) * side]) * 0.5f * side;
                    slopes[at * 3 + 2] = (broad[x + (y + Wrap(z + 1, side) * side) * side] - broad[x + (y + Wrap(z - 1, side) * side) * side]) * 0.5f * side;
                }
        });
        var sizes = new float[slopes.Length];
        for (int n = 0; n < slopes.Length; n++) sizes[n] = MathF.Abs(slopes[n]);
        Array.Sort(sizes);
        steepest = sizes[(int)(sizes.Length * 0.995)];
        var bytes = new byte[count * 4];
        for (int n = 0; n < count; n++)
        {
            bytes[n * 4] = Volume.ToByte(lumps[n]);
            for (int c = 0; c < 3; c++) bytes[n * 4 + 1 + c] = Volume.ToByte(0.5f + 0.5f * slopes[n * 3 + c] / steepest);
        }
        return bytes;
    }

    struct Vector3 { public float X, Y, Z; }

    /// <summary>One random point in every cell of a lattice.</summary>
    static Vector3[] Points(int cells, int seed)
    {
        var random = new Random(seed);
        var points = new Vector3[cells * cells * cells];
        for (int n = 0; n < points.Length; n++) points[n] = new Vector3 { X = (float)random.NextDouble(), Y = (float)random.NextDouble(), Z = (float)random.NextDouble() };
        return points;
    }

    /// <summary>
    /// 1 at a lattice's random points falling towards 0 midway between them: round lumps packed together.
    /// Where two or three points are equally near, the trough between their lumps is rounded off and not
    /// a sharp crease (the nearest distance is taken softly).
    /// </summary>
    static float Cells(Vector3[] points, int cells, float u, float v, float w)
    {
        float x = u * cells, y = v * cells, z = w * cells;
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
        const float sharp = 9f;
        float sum = 0f;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = xi + dx, cy = yi + dy, cz = zi + dz;
                    Vector3 p = points[Wrap(cx, cells) + (Wrap(cy, cells) + Wrap(cz, cells) * cells) * cells];
                    float ex = cx + p.X - x, ey = cy + p.Y - y, ez = cz + p.Z - z;
                    sum += MathF.Exp(-sharp * MathF.Sqrt(ex * ex + ey * ey + ez * ez));
                }
        return Volume.Saturate(1f + MathF.Log(sum) / (sharp * 0.95f));
    }

    static int Wrap(int n, int cells) => ((n % cells) + cells) % cells;

    /// <summary>Smooth noise that repeats every 'cells' lattice steps.</summary>
    static float Tiled(float u, float v, float w, int cells, int seed)
    {
        float x = u * cells, y = v * cells, z = w * cells;
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
        float fx = x - xi, fy = y - yi, fz = z - zi;
        float tx = fx * fx * (3f - 2f * fx), ty = fy * fy * (3f - 2f * fy), tz = fz * fz * (3f - 2f * fz);
        float At(int a, int b, int c) => Noise.Speck(Wrap(xi + a, cells) + Wrap(zi + c, cells) * 131, Wrap(yi + b, cells), seed);
        float low = Volume.Lerp(Volume.Lerp(At(0, 0, 0), At(1, 0, 0), tx), Volume.Lerp(At(0, 1, 0), At(1, 1, 0), tx), ty);
        float high = Volume.Lerp(Volume.Lerp(At(0, 0, 1), At(1, 0, 1), tx), Volume.Lerp(At(0, 1, 1), At(1, 1, 1), tx), ty);
        return Volume.Lerp(low, high, tz);
    }
}

static class Volume
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float Smooth(float t) => t * t * (3f - 2f * t);
    public static byte ToByte(float v) => (byte)(Saturate(v) * 255f + 0.5f);
}

static class Noise
{
    static uint Mix(int x, int y, int z, int seed)
    {
        uint h = (uint)(x * 374761393) ^ (uint)(y * 668265263) ^ (uint)(z * 1440662683) ^ (uint)(seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    static float Hash(int x, int y, int z, int seed) => (Mix(x, y, z, seed) & 0xFFFFFF) / 16777215f;

    /// <summary>An unrelated random value for every pixel.</summary>
    public static float Speck(int x, int y, int seed) => Hash(x, y, 7, seed);

    /// <summary>The slope at a lattice corner, one of twelve directions, dotted with the offset from that corner.</summary>
    static float Slope(int xi, int yi, int zi, int seed, float dx, float dy, float dz)
    {
        switch (Mix(xi, yi, zi, seed) % 12)
        {
            case 0: return dx + dy; case 1: return -dx + dy; case 2: return dx - dy; case 3: return -dx - dy;
            case 4: return dx + dz; case 5: return -dx + dz; case 6: return dx - dz; case 7: return -dx - dz;
            case 8: return dy + dz; case 9: return -dy + dz; case 10: return dy - dz; default: return -dy - dz;
        }
    }

    /// <summary>Smooth noise between 0 and 1 (gradient noise: no features lined up with the grid).</summary>
    public static float Value(float x, float y, float z, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y), zi = (int)MathF.Floor(z);
        float fx = x - xi, fy = y - yi, fz = z - zi;
        float tx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f), ty = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f), tz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);
        float a = Volume.Lerp(
            Volume.Lerp(Slope(xi, yi, zi, seed, fx, fy, fz), Slope(xi + 1, yi, zi, seed, fx - 1, fy, fz), tx),
            Volume.Lerp(Slope(xi, yi + 1, zi, seed, fx, fy - 1, fz), Slope(xi + 1, yi + 1, zi, seed, fx - 1, fy - 1, fz), tx), ty);
        float b = Volume.Lerp(
            Volume.Lerp(Slope(xi, yi, zi + 1, seed, fx, fy, fz - 1), Slope(xi + 1, yi, zi + 1, seed, fx - 1, fy, fz - 1), tx),
            Volume.Lerp(Slope(xi, yi + 1, zi + 1, seed, fx, fy - 1, fz - 1), Slope(xi + 1, yi + 1, zi + 1, seed, fx - 1, fy - 1, fz - 1), tx), ty);
        return Volume.Saturate(0.5f + 0.55f * Volume.Lerp(a, b, tz));
    }

    /// <summary>Layered noise folded so it forms rounded lumps with sharp creases between them, like boiling smoke.</summary>
    public static float Billow(float x, float y, float z, int octaves, int seed)
    {
        float sum = 0f, weight = 0.5f, total = 0f;
        for (int o = 0; o < octaves; o++)
        {
            float n = Value(x, y, z, seed + o * 101);
            sum += weight * MathF.Abs(2f * n - 1f);
            total += weight;
            weight *= 0.5f;
            // Twice as fine, and turned, for the next layer.
            float nx = (0.00f * x + 1.60f * y + 1.20f * z) + 11.3f;
            float ny = (-1.60f * x + 0.72f * y - 0.96f * z) + 4.7f;
            float nz = (-1.20f * x - 0.96f * y + 1.28f * z) + 7.9f;
            x = nx; y = ny; z = nz;
        }
        return 1f - sum / total * 1.9f;      // lumps high, creases low; roughly 0..1
    }
}

/// <summary>The other textures: a flash, a spark, a shock ring, a metal shard and a scorch mark.</summary>
static class Simple
{
    /// <summary>A soft ball of light (for the additive shader: colour and alpha both fall off).</summary>
    public static byte[] Glow(int size)
    {
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float a = MathF.Exp(-r * r * 7f) + 0.35f * MathF.Exp(-r * r * 1.8f);
                a *= Volume.Smooth(Volume.Saturate((1f - r) / 0.25f));
                Set(p, (y * size + x) * 4, 1f, 1f, 1f, a);
            }
        return p;
    }

    /// <summary>A streak, bright at its head, for sparks drawn stretched along their flight.</summary>
    public static byte[] Spark(int size)
    {
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size, v = (y + 0.5f) / size * 2f - 1f;
                float across = MathF.Exp(-v * v * 26f);
                float along = MathF.Pow(u, 1.6f) * Volume.Smooth(Volume.Saturate((1f - u) / 0.12f));
                Set(p, (y * size + x) * 4, 1f, 1f, 1f, across * along);
            }
        return p;
    }

    /// <summary>A thin ring for the blast wave.</summary>
    public static byte[] Ring(int size)
    {
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float d = (r - 0.86f) / 0.045f;
                float a = MathF.Exp(-d * d) + 0.22f * Volume.Saturate((r - 0.45f) / 0.41f) * (r < 0.86f ? 1f : 0f);
                a *= Volume.Smooth(Volume.Saturate((1f - r) / 0.08f));
                Set(p, (y * size + x) * 4, 1f, 1f, 1f, a);
            }
        return p;
    }

    /// <summary>A jagged dark fragment with a lighter edge (ordinary alpha).</summary>
    public static byte[] Shard(int size)
    {
        var p = new byte[size * size * 4];
        float[,] corners = { { -0.72f, -0.30f }, { -0.10f, -0.78f }, { 0.66f, -0.42f }, { 0.80f, 0.22f }, { 0.12f, 0.74f }, { -0.58f, 0.46f } };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float inside = 1e9f;                         // distance to the nearest edge, negative outside
                for (int n = 0; n < 6; n++)
                {
                    int m = (n + 1) % 6;
                    float ex = corners[m, 0] - corners[n, 0], ey = corners[m, 1] - corners[n, 1];
                    float cross = (ex * (v - corners[n, 1]) - ey * (u - corners[n, 0])) / MathF.Sqrt(ex * ex + ey * ey);
                    inside = MathF.Min(inside, cross);
                }
                float a = Volume.Saturate(inside / 0.05f);
                float shade = 0.10f + 0.16f * Volume.Saturate(1f - inside / 0.22f) + 0.10f * Noise.Value(u * 6f, v * 6f, 0.5f, 77);
                Set(p, (y * size + x) * 4, shade, shade * 0.96f, shade * 0.90f, a);
            }
        return p;
    }

    /// <summary>
    /// A burn mark for the ground (ordinary colour and opacity). From the middle outward: a patch charred
    /// black with a ragged edge, soot fading out round it, pale ash lying in patches over the soot, thin
    /// streaks of soot thrown out much further, and spatter.
    /// </summary>
    public static byte[] Scorch(int size)
    {
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float angle = MathF.Atan2(v, u), cu = MathF.Cos(angle), cv = MathF.Sin(angle);
                // Noise taken round a circle joins up with itself.
                float lobes = Noise.Value(cu * 1.7f + 5f, cv * 1.7f + 5f, 0.5f, 31) * 0.6f + Noise.Value(cu * 4.3f + 2f, cv * 4.3f + 2f, 3.5f, 32) * 0.4f;
                float reach = 0.20f + 0.20f * lobes + 0.03f * Noise.Value(u * 9f, v * 9f, 2.5f, 35);
                float rough = Noise.Value(u * 6f, v * 6f, 1.5f, 33), grit = Noise.Value(u * 23f, v * 23f, 6.5f, 34);

                // Soot: strongest by the char, gone by twice as far out, uneven.
                float soot = Volume.Smooth(Volume.Saturate((reach * 2.3f - r) / (reach * 1.6f))) * (0.45f + 0.4f * rough + 0.15f * grit);
                float cr = 0.075f, cg = 0.07f, cb = 0.062f, a = soot * 0.72f;
                // Ash: pale grey, in patches, in the ring where the fire burned longest.
                float ring = Volume.Smooth(Volume.Saturate((r - reach * 0.7f) / (reach * 0.5f))) * Volume.Smooth(Volume.Saturate((reach * 2.0f - r) / (reach * 0.7f)));
                float ash = ring * Volume.Saturate((Noise.Value(u * 11f + 3f, v * 11f - 2f, 9.5f, 36) - 0.5f) / 0.18f) * 0.5f;
                cr = Volume.Lerp(cr, 0.30f, ash); cg = Volume.Lerp(cg, 0.285f, ash); cb = Volume.Lerp(cb, 0.26f, ash);
                a = Volume.Saturate(a + ash * 0.25f);
                // Streaks: many thin rays of different lengths.
                float rays = MathF.Pow(Noise.Value(cu * 19f + 7f, cv * 19f + 7f, 4.5f, 37), 5f) * 3.2f + MathF.Pow(Noise.Value(cu * 47f + 1f, cv * 47f + 1f, 8.5f, 38), 6f) * 2.2f;
                float length = 0.55f + 0.45f * Noise.Value(cu * 9f + 4f, cv * 9f + 4f, 6.5f, 39);
                float streak = Volume.Saturate(rays) * Volume.Smooth(Volume.Saturate((length - r) / (length * 0.75f))) * Volume.Saturate((r - reach * 0.5f) / (reach * 0.5f));
                a = Volume.Saturate(a + streak * 0.6f * (1f - a));
                cr = Volume.Lerp(cr, 0.05f, streak * 0.7f); cg = Volume.Lerp(cg, 0.047f, streak * 0.7f); cb = Volume.Lerp(cb, 0.042f, streak * 0.7f);
                // The char itself.
                float charred = Volume.Smooth(Volume.Saturate((reach - r) / (reach * 0.45f)));
                a = Volume.Lerp(a, 0.93f, charred * (0.85f + 0.15f * grit));
                cr = Volume.Lerp(cr, 0.022f, charred); cg = Volume.Lerp(cg, 0.02f, charred); cb = Volume.Lerp(cb, 0.018f, charred);
                // Spatter: specks, fewer further out.
                float speck = Volume.Saturate((Noise.Value(u * 41f, v * 41f, 3.5f, 40) - 0.74f) / 0.05f) * Volume.Saturate((0.95f - r) / 0.6f) * Volume.Saturate((r - reach * 0.8f) / 0.1f);
                a = Volume.Saturate(a + speck * 0.55f * (1f - a));
                a *= Volume.Smooth(Volume.Saturate((1f - r) / 0.08f));
                Set(p, (y * size + x) * 4, cr, cg, cb, a);
            }
        return p;
    }

    /// <summary>What still glows in a fresh burn mark: embers, thick in the middle, in a broken pattern (for the additive shader).</summary>
    public static byte[] Embers(int size)
    {
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float cracks = 1f - MathF.Abs(2f * Noise.Value(u * 7f, v * 7f, 2.5f, 51) - 1f);
                float specks = Volume.Saturate((Noise.Value(u * 26f, v * 26f, 5.5f, 52) - 0.62f) / 0.1f);
                float body = MathF.Exp(-r * r * 9f);
                float glow = Volume.Saturate(body * (0.25f + 1.4f * MathF.Pow(cracks, 5f)) + body * specks * 0.8f + MathF.Exp(-r * r * 30f) * 0.5f);
                Set(p, (y * size + x) * 4, 1f, 0.42f + 0.4f * glow, 0.08f + 0.25f * glow * glow, glow);
            }
        return p;
    }

    /// <summary>A premultiplied picture laid over a plain background, for looking at.</summary>
    public static byte[] Over(byte[] src, int width, int height, int r, int g, int b)
    {
        var p = new byte[src.Length];
        for (int n = 0; n < width * height; n++)
        {
            float keep = 1f - src[n * 4 + 3] / 255f;
            p[n * 4] = (byte)Math.Min(255f, src[n * 4] + r * keep);
            p[n * 4 + 1] = (byte)Math.Min(255f, src[n * 4 + 1] + g * keep);
            p[n * 4 + 2] = (byte)Math.Min(255f, src[n * 4 + 2] + b * keep);
            p[n * 4 + 3] = 255;
        }
        return p;
    }

    static void Set(byte[] p, int at, float r, float g, float b, float a)
    {
        p[at] = Volume.ToByte(r);
        p[at + 1] = Volume.ToByte(g);
        p[at + 2] = Volume.ToByte(b);
        p[at + 3] = Volume.ToByte(a);
    }
}

/// <summary>A minimal PNG writer (8-bit RGBA). Rows are given bottom-up, the way Unity numbers them.</summary>
static class Png
{
    public static void Write(string path, byte[] rgba, int width, int height)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; header[9] = 6;
        Chunk(file, "IHDR", header);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, true))
        {
            for (int y = height - 1; y >= 0; y--)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba, y * width * 4, width * 4);
            }
        }
        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    static void Chunk(Stream file, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        file.Write(length);
        var body = new byte[4 + data.Length];
        for (int n = 0; n < 4; n++) body[n] = (byte)type[n];
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        file.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        var check = new byte[4];
        WriteInt(check, 0, (int)(crc ^ 0xFFFFFFFF));
        file.Write(check);
    }

    static void WriteInt(byte[] into, int at, int value)
    {
        into[at] = (byte)(value >> 24); into[at + 1] = (byte)(value >> 16); into[at + 2] = (byte)(value >> 8); into[at + 3] = (byte)value;
    }
}
