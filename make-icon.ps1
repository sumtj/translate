# make-icon.ps1 - generate the multi-size app icon (build/app.ico)
# ASCII only on purpose: PowerShell 5.1 reads BOM-less files as GBK, and non-ASCII
# would break parsing. Keep this file free of non-ASCII characters.
#
# Design: rounded blue-gradient square + white speech bubble + letter "A".
# No CJK glyphs. Sizes <= 20 drop the bubble and enlarge the A so it stays legible.
#
# Why a C# helper instead of plain PowerShell: assembling the ICO byte layout in
# PowerShell proved unreliable (the body write silently truncated). C# gives exact
# control over the byte array.

param(
    [string]$Out = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'build\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class IconMaker
{
    // ---------- drawing ----------
    public static Bitmap Draw(int s)
    {
        Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        // rounded square with blue gradient
        float r = s * 0.235f;
        float d = r * 2f;
        GraphicsPath bg = new GraphicsPath();
        bg.AddArc(0, 0, d, d, 180, 90);
        bg.AddArc(s - d, 0, d, d, 270, 90);
        bg.AddArc(s - d, s - d, d, d, 0, 90);
        bg.AddArc(0, s - d, d, d, 90, 90);
        bg.CloseFigure();

        LinearGradientBrush grad = new LinearGradientBrush(
            new Rectangle(0, 0, s, s),
            Color.FromArgb(255, 90, 168, 255),
            Color.FromArgb(255, 6, 84, 190),
            55f);
        g.FillPath(grad, bg);

        SolidBrush white = new SolidBrush(Color.White);
        SolidBrush deep = new SolidBrush(Color.FromArgb(255, 10, 74, 160));
        StringFormat fmt = new StringFormat();
        fmt.Alignment = StringAlignment.Center;
        fmt.LineAlignment = StringAlignment.Center;

        if (s <= 16)
        {
            // 16px is too small for the bubble, so use a plain white "A".
            // Keep it at ~60% of the tile: at 74% the white glyph dominated and the
            // whole icon read as a white square instead of a blue one.
            Font f = new Font("Segoe UI", s * 0.60f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("A", f, white, new RectangleF(0, 0, s, s), fmt);
            f.Dispose();
        }
        else
        {
            // large: white speech bubble with a deep-blue "A" inside
            float bx = s * 0.155f, by = s * 0.185f;
            float bw = s * 0.690f, bh = s * 0.475f;
            float br = s * 0.135f, bd = br * 2f;

            GraphicsPath bubble = new GraphicsPath();
            bubble.AddArc(bx, by, bd, bd, 180, 90);
            bubble.AddArc(bx + bw - bd, by, bd, bd, 270, 90);
            bubble.AddArc(bx + bw - bd, by + bh - bd, bd, bd, 0, 90);
            bubble.AddArc(bx, by + bh - bd, bd, bd, 90, 90);
            bubble.CloseFigure();
            g.FillPath(white, bubble);

            GraphicsPath tail = new GraphicsPath();
            tail.AddPolygon(new PointF[] {
                new PointF(s * 0.275f, s * 0.645f),
                new PointF(s * 0.400f, s * 0.645f),
                new PointF(s * 0.255f, s * 0.820f)
            });
            g.FillPath(white, tail);

            Font f = new Font("Segoe UI", s * 0.330f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("A", f, deep, new RectangleF(bx, by, bw, bh), fmt);
            f.Dispose();
            bubble.Dispose();
            tail.Dispose();
        }

        white.Dispose(); deep.Dispose(); fmt.Dispose(); grad.Dispose(); bg.Dispose(); g.Dispose();
        return bmp;
    }

    // ---------- ICO entry as a DIB (BITMAPINFOHEADER + bottom-up BGRA + AND mask) ----------
    // .NET Framework 4.8's System.Drawing.Icon cannot read PNG-compressed entries,
    // so every size except 256 uses this classic format.
    public static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] pixels = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        int maskRow = ((w + 31) / 32) * 4;
        int maskSize = maskRow * h;

        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        bw.Write((uint)40);              // biSize
        bw.Write((int)w);                // biWidth
        bw.Write((int)(h * 2));          // biHeight = XOR + AND
        bw.Write((ushort)1);             // biPlanes
        bw.Write((ushort)32);            // biBitCount
        bw.Write((uint)0);               // biCompression = BI_RGB
        bw.Write((uint)(w * h * 4));     // biSizeImage
        bw.Write((int)0); bw.Write((int)0);
        bw.Write((uint)0); bw.Write((uint)0);
        for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, w * 4);
        bw.Write(new byte[maskSize]);    // AND mask all zero (alpha channel is used)
        bw.Flush();
        byte[] result = ms.ToArray();
        bw.Dispose(); ms.Dispose();
        return result;
    }

    public static byte[] Png(Bitmap bmp)
    {
        MemoryStream ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        byte[] result = ms.ToArray();
        ms.Dispose();
        return result;
    }

    // ---------- assemble the .ico ----------
    public static int Write(string path, int[] sizes)
    {
        List<int> dims = new List<int>();
        List<byte[]> blobs = new List<byte[]>();

        foreach (int s in sizes)
        {
            Bitmap bmp = Draw(s);
            // 64 and up are stored as PNG (they would be huge as uncompressed DIBs,
            // and Explorer reads PNG entries fine). Sizes we actually request at
            // runtime (tray / taskbar, <= 48) must be DIB because .NET Framework's
            // System.Drawing.Icon cannot decode PNG entries.
            blobs.Add(s >= 64 ? Png(bmp) : Dib(bmp));
            dims.Add(s);
            bmp.Dispose();
        }

        MemoryStream outMs = new MemoryStream();
        BinaryWriter w = new BinaryWriter(outMs);
        w.Write((ushort)0);                  // reserved
        w.Write((ushort)1);                  // type = icon
        w.Write((ushort)blobs.Count);

        int offset = 6 + 16 * blobs.Count;
        for (int i = 0; i < blobs.Count; i++)
        {
            int dim = dims[i] >= 256 ? 0 : dims[i];
            w.Write((byte)dim);
            w.Write((byte)dim);
            w.Write((byte)0);                // color count
            w.Write((byte)0);                // reserved
            w.Write((ushort)1);              // planes
            w.Write((ushort)32);             // bit count
            w.Write((uint)blobs[i].Length);
            w.Write((uint)offset);
            offset += blobs[i].Length;
        }
        for (int i = 0; i < blobs.Count; i++) w.Write(blobs[i], 0, blobs[i].Length);
        w.Flush();

        byte[] all = outMs.ToArray();
        w.Dispose(); outMs.Dispose();

        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, all);
        return blobs.Count;
    }
}
'@ -ReferencedAssemblies 'System.Drawing'

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$n = [IconMaker]::Write($Out, @(16, 20, 24, 32, 48, 64, 128, 256))
$kb = [math]::Round((Get-Item $Out).Length / 1KB, 1)
Write-Host ("  icon: {0} sizes, {1} KB -> {2}" -f $n, $kb, $Out) -ForegroundColor DarkGray
