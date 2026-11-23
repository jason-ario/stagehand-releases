using System;
using System.Collections.Generic;

namespace HyperBox.Export
{
    /// <summary>One square view of the sky rendered in Unity: the camera's axes in Unity world
    /// space and its pixels (RGB floats 0..1, row 0 at the bottom, as Texture2D.GetPixels gives).</summary>
    public class SkyFace
    {
        public double[] Forward, Right, Up;
        public double TanHalfFov = 1;   // 90° field of view
        public int Size;
        public float[] Rgb;             // Size * Size * 3
    }

    /// <summary>
    /// Turns six views of a Unity skybox into one equirectangular panorama in Godot's convention
    /// (measured in Godot 4.5): u = 0 looks down -Z, 0.25 +X, 0.5 +Z, 0.75 -X; the top row looks
    /// straight up. Directions are in HyperBox space, which mirrors Unity's X. Pure C#.
    /// </summary>
    public static class SkyResample
    {
        /// <summary>HyperBox-space direction seen at panorama pixel (col, row), row 0 = top.</summary>
        public static double[] Direction(int col, int row, int width, int height)
        {
            var theta = (col + 0.5) / width * 2 * Math.PI;   // atan2(x, -z)
            var phi = (row + 0.5) / height * Math.PI;        // acos(y)
            var s = Math.Sin(phi);
            return new[] { s * Math.Sin(theta), Math.Cos(phi), -s * Math.Cos(theta) };
        }

        /// <summary>The panorama as RGB24 bytes with the bottom row first (ready for a Unity
        /// Texture2D, which encodes to a PNG with the top row first).</summary>
        public static byte[] ToEquirect(IList<SkyFace> faces, int width, int height)
        {
            var output = new byte[width * height * 3];
            var rgb = new double[3];
            for (var row = 0; row < height; row++)
            {
                var texRow = height - 1 - row;
                for (var col = 0; col < width; col++)
                {
                    var d = Direction(col, row, width, height);
                    Sample(faces, -d[0], d[1], d[2], rgb);   // HyperBox -> Unity: mirror X
                    var o = (texRow * width + col) * 3;
                    for (var c = 0; c < 3; c++)
                        output[o + c] = (byte)Math.Round(Math.Max(0, Math.Min(1, rgb[c])) * 255);
                }
            }
            return output;
        }

        /// <summary>Colour of the Unity-space direction (x, y, z), from the view that faces it most.</summary>
        public static void Sample(IList<SkyFace> faces, double x, double y, double z, double[] rgb)
        {
            SkyFace best = null;
            var bestDot = 0.0;
            foreach (var f in faces)
            {
                var k = x * f.Forward[0] + y * f.Forward[1] + z * f.Forward[2];
                if (k > bestDot) { bestDot = k; best = f; }
            }
            rgb[0] = rgb[1] = rgb[2] = 0;
            if (best == null) return;
            var cx = (x * best.Right[0] + y * best.Right[1] + z * best.Right[2]) / bestDot / best.TanHalfFov;
            var cy = (x * best.Up[0] + y * best.Up[1] + z * best.Up[2]) / bestDot / best.TanHalfFov;
            var px = (cx * 0.5 + 0.5) * best.Size - 0.5;
            var py = (cy * 0.5 + 0.5) * best.Size - 0.5;
            Bilinear(best, px, py, rgb);
        }

        static void Bilinear(SkyFace f, double px, double py, double[] rgb)
        {
            var n = f.Size;
            px = Math.Max(0, Math.Min(n - 1, px));
            py = Math.Max(0, Math.Min(n - 1, py));
            int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
            int x1 = Math.Min(n - 1, x0 + 1), y1 = Math.Min(n - 1, y0 + 1);
            double tx = px - x0, ty = py - y0;
            for (var c = 0; c < 3; c++)
            {
                var a = f.Rgb[(y0 * n + x0) * 3 + c] * (1 - tx) + f.Rgb[(y0 * n + x1) * 3 + c] * tx;
                var b = f.Rgb[(y1 * n + x0) * 3 + c] * (1 - tx) + f.Rgb[(y1 * n + x1) * 3 + c] * tx;
                rgb[c] = a * (1 - ty) + b * ty;
            }
        }

        /// <summary>The six views to render: forward and up vectors in Unity space.</summary>
        public static readonly double[][][] Views =
        {
            new[] { new double[] { 0, 0, 1 }, new double[] { 0, 1, 0 } },
            new[] { new double[] { 0, 0, -1 }, new double[] { 0, 1, 0 } },
            new[] { new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 } },
            new[] { new double[] { -1, 0, 0 }, new double[] { 0, 1, 0 } },
            new[] { new double[] { 0, 1, 0 }, new double[] { 0, 0, -1 } },
            new[] { new double[] { 0, -1, 0 }, new double[] { 0, 0, 1 } },
        };
    }
}
