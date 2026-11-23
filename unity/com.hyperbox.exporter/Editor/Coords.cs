using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HyperBox.Export
{
    /// <summary>A rotation as a quaternion, in plain doubles (no UnityEngine types, so this file
    /// can be unit-tested outside Unity).</summary>
    public struct Quat
    {
        public double x, y, z, w;
        public Quat(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly Quat Identity = new Quat(0, 0, 0, 1);
        /// <summary>Half a turn around +Y.</summary>
        public static readonly Quat TurnY180 = new Quat(0, 1, 0, 0);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public Quat Normalized()
        {
            var n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return n < 1e-12 ? Identity : new Quat(x / n, y / n, z / n, w / n);
        }
    }

    /// <summary>
    /// Unity → HyperBox coordinate conversion.
    ///
    /// Unity is left-handed (+X right, +Y up, +Z forward). HyperBox uses Godot's right-handed
    /// space (Y up). UnityGLTF converts meshes by mirroring X, so documents use the same mirror:
    /// positions (x, y, z) → (-x, y, z), rotations mirror(q).
    ///
    /// Models face +Z in Unity, and still face +Z after the mirror, which is what HyperBox
    /// expects of models (glTF's convention; face / walk_to use yaw = atan2(dx, dz)). So a
    /// model's document rotation is just mirror(unity rotation).
    /// Cameras and lights point down -Z in Godot but +Z in Unity, so theirs is
    /// mirror(unity rotation) · TurnY180.
    /// </summary>
    public static class Coords
    {
        public static double[] Position(double x, double y, double z) => new[] { R(-x), R(y), R(z) };

        /// <summary>A Unity rotation as seen in the X-mirrored space.</summary>
        public static Quat Mirror(Quat q) => new Quat(q.x, -q.y, -q.z, q.w);

        /// <summary>Document rotation for a model (models face +Z in both Unity and HyperBox).</summary>
        public static Quat ModelRotation(Quat unityWorld) => Mirror(unityWorld.Normalized());

        /// <summary>Document rotation for a camera or light (they point down -Z in HyperBox).</summary>
        public static Quat FacingRotation(Quat unityWorld) => (Mirror(unityWorld.Normalized()) * Quat.TurnY180).Normalized();

        /// <summary>Euler angles in degrees, in Godot's default order (YXZ), as Node3D.rotation_degrees reads them.</summary>
        public static double[] EulerYXZDegrees(Quat q)
        {
            q = q.Normalized();
            double xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
            double xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
            double wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;
            double m00 = 1 - 2 * (yy + zz), m01 = 2 * (xy - wz), m02 = 2 * (xz + wy);
            double m10 = 2 * (xy + wz), m11 = 1 - 2 * (xx + zz), m12 = 2 * (yz - wx);
            double m22 = 1 - 2 * (xx + yy);
            double ex, ey, ez;
            const double eps = 1e-6;
            if (m12 < 1 - eps)
            {
                if (m12 > -(1 - eps))
                {
                    ex = Math.Asin(-m12);
                    ey = Math.Atan2(m02, m22);
                    ez = Math.Atan2(m10, m11);
                }
                else
                {
                    ex = Math.PI * 0.5;
                    ey = Math.Atan2(m01, m00);
                    ez = 0;
                }
            }
            else
            {
                ex = -Math.PI * 0.5;
                ey = -Math.Atan2(m01, m00);
                ez = 0;
            }
            return new[] { Deg(ex), Deg(ey), Deg(ez) };
        }

        /// <summary>Sun elevation and azimuth (degrees) for a Unity directional light shining along `forward`.</summary>
        public static double[] SunAngles(double fx, double fy, double fz)
        {
            var len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (len < 1e-9) return new[] { 45.0, 0.0 };
            fx /= len; fy /= len; fz /= len;
            // Godot's sun with rotation (-elevation, azimuth, 0) shines along
            // (-cos e sin a, -sin e, -cos e cos a); the mirrored Unity direction is (-fx, fy, fz).
            var elevation = Math.Asin(Math.Max(-1, Math.Min(1, -fy)));
            var azimuth = Math.Abs(Math.Cos(elevation)) < 1e-6 ? 0.0 : Math.Atan2(fx, -fz);
            return new[] { Deg(elevation), Deg(azimuth) };
        }

        public static string Hex(double r, double g, double b)
        {
            int C(double v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", C(r), C(g), C(b));
        }

        /// <summary>A document id (^[a-z][a-z0-9_]*$) from a Unity name, unique within `used`.</summary>
        public static string Id(string name, HashSet<string> used, string fallback = "item")
        {
            var sb = new StringBuilder();
            var lastUnderscore = true;
            foreach (var ch in (name ?? "").ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }
            var id = sb.ToString().Trim('_');
            if (id.Length > 40) id = id.Substring(0, 40).TrimEnd('_');
            if (id.Length == 0) id = fallback;
            if (!(id[0] >= 'a' && id[0] <= 'z')) id = fallback + "_" + id;
            var unique = id;
            for (var n = 2; used.Contains(unique); n++) unique = id + "_" + n;
            used.Add(unique);
            return unique;
        }

        /// <summary>A file name (no extension) safe on every OS, unique within `used`.</summary>
        public static string FileName(string name, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (var ch in name ?? "")
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            var baseName = sb.ToString().Trim('_');
            if (baseName.Length == 0) baseName = "model";
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60);
            var unique = baseName;
            for (var n = 2; used.Contains(unique.ToLowerInvariant()); n++) unique = baseName + "_" + n;
            used.Add(unique.ToLowerInvariant());
            return unique;
        }

        /// <summary>How Godot names a skeleton bone imported from a glTF node called `name`.</summary>
        public static string GodotBoneName(string name) => (name ?? "").Replace(':', '_').Replace('/', '_');

        /// <summary>How Godot names an animation imported from a glTF animation called `name`
        /// (Godot 4.5 replaces / : , [ with _, e.g. "Run/Fast:1" → "Run_Fast_1").</summary>
        public static string GodotAnimationName(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name ?? "")
                sb.Append(ch == '/' || ch == ':' || ch == ',' || ch == '[' ? '_' : ch);
            return sb.ToString();
        }

        /// <summary>HyperBox's standard bone name for a Unity HumanBodyBones name, or null for
        /// bones HyperBox doesn't use (eyes, jaw). Unity counts thumb joints from the base
        /// (Proximal, Intermediate, Distal); HyperBox uses Metacarpal, Proximal, Distal.</summary>
        public static string HyperBoxBone(string unityBone)
        {
            if (string.IsNullOrEmpty(unityBone) || unityBone == "LastBone" || unityBone.EndsWith("Eye") || unityBone == "Jaw")
                return null;
            foreach (var side in new[] { "Left", "Right" })
            {
                if (unityBone == side + "ThumbProximal") return side + "ThumbMetacarpal";
                if (unityBone == side + "ThumbIntermediate") return side + "ThumbProximal";
            }
            return unityBone;
        }

        public static double R(double v)
        {
            var r = Math.Round(v, 4);
            return r == 0 ? 0 : r;  // no "-0"
        }

        static double Deg(double rad) => R(rad * 180.0 / Math.PI);
    }
}
