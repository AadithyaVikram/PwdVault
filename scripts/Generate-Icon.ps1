# Windows-only raster export of the original vector logo.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class PwdVaultIconExport
{
    static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static byte[] Render(int size)
    {
        using (var source = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(source))
            using (var navy = new SolidBrush(Color.FromArgb(21, 31, 45)))
            using (var mint = new SolidBrush(Color.FromArgb(102, 217, 180)))
            using (var pen = new Pen(mint, 16))
            using (var background = Rounded(8, 8, 240, 240, 52))
            using (var body = Rounded(64, 104, 128, 112, 24))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size * 4f / 256, size * 4f / 256);
                g.FillPath(navy, background);
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 88, 112, 88, 88);
                g.DrawArc(pen, 88, 48, 80, 80, 180, 180);
                g.DrawLine(pen, 168, 88, 168, 112);
                g.FillPath(mint, body);
                g.FillEllipse(navy, 115, 135, 26, 26);
                g.FillPolygon(navy, new [] { new Point(123,156), new Point(133,156), new Point(138,183), new Point(118,183) });
            }
            using (var result = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(source, 0, 0, size, size);
                }
                using (var stream = new MemoryStream())
                {
                    result.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
    }

    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = Render(sizes[i]);
        File.WriteAllBytes(Path.Combine(directory, "logo.png"), images[images.Length - 1]);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "pwdvault.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[i].Length); writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] png in images) writer.Write(png);
        }
    }
}
'@
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PwdVault.Desktop/Assets'
[PwdVaultIconExport]::Export($assets)
Write-Output 'Generated logo.png and multi-resolution pwdvault.ico.'
