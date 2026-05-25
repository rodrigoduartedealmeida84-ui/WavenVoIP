using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// ─── Paths ────────────────────────────────────────────────────────────────────
var rootDir = Directory.GetCurrentDirectory();  // WavenVoIP_v79 root (dotnet run)
var srcPng  = Path.Combine(rootDir, "WavenVoIP", "Assets", "ICONE WAVEN VOIP.png");
var outDir  = Path.Combine(rootDir, "Tools", "GenerateIcon", "output_v4");
var webDir  = Path.Combine(rootDir, "waven_site_premium", "assets", "icons");

Directory.CreateDirectory(outDir);
Directory.CreateDirectory(webDir);

if (!File.Exists(srcPng))
{
    Console.Error.WriteLine($"ERRO: imagem nao encontrada: {srcPng}");
    Environment.Exit(1);
}

Console.WriteLine("=== WavenVoIP Icon Generator v4 — Official PNG → ICO ===\n");
Console.WriteLine($"Source: {srcPng}");

// ─── Load and crop to square ─────────────────────────────────────────────────
using var rawBmp   = (Bitmap)Image.FromFile(srcPng);
Console.WriteLine($"  Original: {rawBmp.Width}×{rawBmp.Height}  [{rawBmp.PixelFormat}]");

using var squareBmp = CropToSquare(rawBmp);
Console.WriteLine($"  Cropped:  {squareBmp.Width}×{squareBmp.Height}  (square, centrado)");

// ─── Remove residual near-white fringe (flood-fill from edges if needed) ─────
// The PNG already has alpha=0 at edges, so this is a no-op but safe to call.
using var cleanBmp = EnsureCleanAlpha(squareBmp);
Console.WriteLine($"  Alpha:    clean\n");

// ─── Generate all ICO sizes ───────────────────────────────────────────────────
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
var icoEntries = new List<(int sz, byte[] png)>();

Console.WriteLine("--- Tamanhos gerados ---");
foreach (int sz in icoSizes)
{
    using var scaled = ScaleHQ(cleanBmp, sz);
    using var ms     = new MemoryStream();
    scaled.Save(ms, ImageFormat.Png);
    var data = ms.ToArray();

    icoEntries.Add((sz, data));
    File.WriteAllBytes(Path.Combine(outDir, $"waven_icon_{sz}.png"), data);
    Console.WriteLine($"  {sz,3}×{sz,-3}  ({data.Length / 1024f:F1} KB)");
}

// ─── Write wavenvoip.ico ─────────────────────────────────────────────────────
var icoPath = Path.Combine(outDir, "wavenvoip.ico");
WriteIco(icoEntries, icoPath);
var icoKB = new FileInfo(icoPath).Length / 1024;
Console.WriteLine($"\n  wavenvoip.ico → {icoKB} KB  ({icoSizes.Length} sizes: {string.Join(", ", icoSizes)})");

// ─── Write favicon.ico (16+32+48) ────────────────────────────────────────────
var faviconEntries = new List<(int, byte[])>();
foreach (int sz in new[] { 16, 32, 48 })
{
    using var b  = ScaleHQ(cleanBmp, sz);
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    faviconEntries.Add((sz, ms.ToArray()));
}
WriteIco(faviconEntries, Path.Combine(outDir,  "favicon.ico"));
WriteIco(faviconEntries, Path.Combine(webDir,  "favicon.ico"));
Console.WriteLine("  favicon.ico   (16+32+48)");

// ─── Web PNGs ─────────────────────────────────────────────────────────────────
Console.WriteLine("\n--- Web assets ---");
(int sz, string name)[] webAssets = {
    (16,  "favicon-16.png"),
    (32,  "favicon-32.png"),
    (180, "apple-touch-icon.png"),
    (256, "android-chrome-256x256.png"),
    (512, "android-chrome-512x512.png"),
};
foreach (var (sz, name) in webAssets)
{
    using var b = ScaleHQ(cleanBmp, sz);
    b.Save(Path.Combine(outDir, name), ImageFormat.Png);
    b.Save(Path.Combine(webDir, name), ImageFormat.Png);
    Console.WriteLine($"  {name}");
}

Console.WriteLine($"\nPronto. Arquivos em:\n  {outDir}\n  {webDir}\n");

// ═════════════════════════════════════════════════════════════════════════════
// CROP TO SQUARE — finds bounding box of opaque content, returns square crop
// ═════════════════════════════════════════════════════════════════════════════

static Bitmap CropToSquare(Bitmap src)
{
    int W = src.Width, H = src.Height;

    // Read all pixels into int[] for fast access
    var srcData = src.LockBits(new Rectangle(0, 0, W, H),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new int[W * H];
    Marshal.Copy(srcData.Scan0, pixels, 0, pixels.Length);
    src.UnlockBits(srcData);

    // Find bounding box of pixels with alpha > 8
    int minX = W, maxX = 0, minY = H, maxY = 0;
    for (int i = 0; i < pixels.Length; i++)
    {
        int a = (pixels[i] >> 24) & 0xFF;
        if (a > 8)
        {
            int x = i % W, y = i / W;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }

    if (maxX <= minX || maxY <= minY) return new Bitmap(src);  // nothing found

    // Add 2px safety margin and clamp to image bounds
    minX = Math.Max(0, minX - 2);
    minY = Math.Max(0, minY - 2);
    maxX = Math.Min(W - 1, maxX + 2);
    maxY = Math.Min(H - 1, maxY + 2);

    int cropW = maxX - minX + 1;
    int cropH = maxY - minY + 1;
    int side  = Math.Max(cropW, cropH); // make it square using max dimension

    // Center the content within the square
    int offX = (side - cropW) / 2;
    int offY = (side - cropH) / 2;

    var result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(result);
    g.Clear(Color.Transparent);
    g.SmoothingMode      = SmoothingMode.AntiAlias;
    g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
    g.DrawImage(src,
        new Rectangle(offX, offY, cropW, cropH),
        new Rectangle(minX, minY, cropW, cropH),
        GraphicsUnit.Pixel);

    return result;
}

// ═════════════════════════════════════════════════════════════════════════════
// ENSURE CLEAN ALPHA — removes any residual near-white fringe pixels
// that may appear at the anti-aliased edges between icon and background.
// ═════════════════════════════════════════════════════════════════════════════

static Bitmap EnsureCleanAlpha(Bitmap src)
{
    int W = src.Width, H = src.Height;

    var srcData = src.LockBits(new Rectangle(0, 0, W, H),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new int[W * H];
    Marshal.Copy(srcData.Scan0, pixels, 0, pixels.Length);
    src.UnlockBits(srcData);

    // Flood fill from edges: remove "white-ish and transparent-ish" background pixels.
    // Since alpha is already 0 at background edges, this mainly cleans up any fringe.
    var visited = new bool[pixels.Length];
    var stack   = new Stack<int>(W * 4);

    for (int x = 0; x < W; x++) { stack.Push(x); stack.Push(x + (H-1)*W); }
    for (int y = 1; y < H-1; y++) { stack.Push(y*W); stack.Push(y*W + W-1); }

    while (stack.Count > 0)
    {
        int idx = stack.Pop();
        if (idx < 0 || idx >= pixels.Length || visited[idx]) continue;
        visited[idx] = true;

        int p = pixels[idx];
        int a = (p >> 24) & 0xFF;
        int r = (p >> 16) & 0xFF;
        int g = (p >> 8)  & 0xFF;
        int b =  p        & 0xFF;

        // Background criterion: low alpha OR very bright (near-white fringe)
        float brightness = (r + g + b) / (3f * 255f);
        bool isBackground = a < 20 || (a < 200 && brightness > 0.88f);

        if (isBackground)
        {
            pixels[idx] = 0; // fully transparent

            int x = idx % W, y = idx / W;
            if (x > 0)   stack.Push(idx - 1);
            if (x < W-1) stack.Push(idx + 1);
            if (y > 0)   stack.Push(idx - W);
            if (y < H-1) stack.Push(idx + W);
        }
    }

    var result = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    var dstData = result.LockBits(new Rectangle(0, 0, W, H),
        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    Marshal.Copy(pixels, 0, dstData.Scan0, pixels.Length);
    result.UnlockBits(dstData);
    return result;
}

// ═════════════════════════════════════════════════════════════════════════════
// HIGH-QUALITY SCALE to target size (preserving alpha channel)
// ═════════════════════════════════════════════════════════════════════════════

static Bitmap ScaleHQ(Bitmap src, int size)
{
    var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.Clear(Color.Transparent);
    g.SmoothingMode      = SmoothingMode.AntiAlias;
    g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.CompositingMode    = CompositingMode.SourceOver;
    g.DrawImage(src, 0, 0, size, size);
    return dst;
}

// ═════════════════════════════════════════════════════════════════════════════
// ICO WRITER — PNG-inside-ICO (Windows Vista+ compatible, all sizes)
// ═════════════════════════════════════════════════════════════════════════════

static void WriteIco(List<(int sz, byte[] png)> images, string path)
{
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var bw = new BinaryWriter(fs);

    int count  = images.Count;
    int header = 6 + count * 16;
    int offset = header;

    var offsets = new List<int>();
    foreach (var (_, d) in images) { offsets.Add(offset); offset += d.Length; }

    bw.Write((ushort)0);      // reserved
    bw.Write((ushort)1);      // ICO type
    bw.Write((ushort)count);

    for (int i = 0; i < count; i++)
    {
        var (sz, d) = images[i];
        bw.Write((byte)(sz >= 256 ? 0 : sz)); // width  (0 = 256)
        bw.Write((byte)(sz >= 256 ? 0 : sz)); // height (0 = 256)
        bw.Write((byte)0);    // palette
        bw.Write((byte)0);    // reserved
        bw.Write((ushort)1);  // color planes
        bw.Write((ushort)32); // bpp
        bw.Write((uint)d.Length);
        bw.Write((uint)offsets[i]);
    }

    foreach (var (_, d) in images)
        bw.Write(d);
}
