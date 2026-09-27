// Headless stand-in for Unity's "UnityEngine" facade, covering exactly the surface Fusion 2.1.2's
// Runtime/Sockets/Common/Log assemblies reference (see ../unity-refs.txt). Math + memory + JSON are
// real managed implementations; scene/physics/animator/gizmo members are inert stubs the server
// simulation never reaches (Shared-mode server has no GameObjects).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unity.Collections { public enum Allocator { Invalid, None, Temp, TempJob, Persistent, AudioKernel } }

namespace Unity.Collections.LowLevel.Unsafe {
    public static unsafe class UnsafeUtility {
        public static void* Malloc(long size, int alignment, Collections.Allocator allocator)
            => NativeMemory.AlignedAlloc((nuint)Math.Max(size, 1), (nuint)Math.Max(alignment, 1));
        public static void Free(void* memory, Collections.Allocator allocator) { if (memory != null) NativeMemory.AlignedFree(memory); }
        public static void MemCpy(void* destination, void* source, long size) => Buffer.MemoryCopy(source, destination, size, size);
        public static void MemMove(void* destination, void* source, long size) => Buffer.MemoryCopy(source, destination, size, size);
        public static void MemSet(void* destination, byte value, long size) => NativeMemory.Fill(destination, (nuint)size, value);
        public static void MemClear(void* destination, long size) => NativeMemory.Clear(destination, (nuint)size);
        public static int MemCmp(void* ptr1, void* ptr2, long size) {
            var a = new ReadOnlySpan<byte>(ptr1, (int)size); var b = new ReadOnlySpan<byte>(ptr2, (int)size);
            return a.SequenceCompareTo(b);
        }
        public static int SizeOf<T>() where T : struct => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        public static int SizeOf(Type type) => Marshal.SizeOf(type);
        public static int EnumToInt<T>(T enumValue) where T : struct, Enum => Convert.ToInt32(enumValue);
        public static int GetFieldOffset(FieldInfo field) => throw new NotSupportedException("UnsafeUtility.GetFieldOffset (headless stub)");
        // Headless heuristic: Fusion calls this to validate its own INetworkStruct types, which are
        // all unmanaged value types. Good enough for the server (no reference-field structs reach here).
        public static bool IsUnmanaged(Type type) => type is { IsValueType: true };
    }
}

namespace Unity.Profiling.LowLevel {
    [Flags] public enum MarkerFlags : ushort { Default = 0, Script = 2, ScriptInvoke = 0x20, ScriptDeepProfiler = 0x40, AvailabilityEditor = 4, AvailabilityNonDevelopment = 8, Warning = 0x10, Counter = 0x80, SampleGPU = 0x100 }
    public enum ProfilerMarkerDataType : byte { InstanceId = 1, Int32 = 2, UInt32 = 3, Int64 = 4, UInt64 = 5, Float = 6, Double = 7, String16 = 9, Blob8 = 11, GpuAllocationId = 12, Count = 13 }
}
namespace Unity.Profiling.LowLevel.Unsafe {
    public static unsafe class ProfilerUnsafeUtility {
        public static void BeginSample(IntPtr markerPtr) { }
        public static void EndSample(IntPtr markerPtr) { }
        public static void* CreateCounterValue(ref IntPtr counterPtr, string name, ushort category, LowLevel.MarkerFlags flags, byte dataType, byte dataUnit, int dataSize, ProfilerCounterOptions counterOptions)
            => NativeMemory.AllocZeroed((nuint)Math.Max(dataSize, 8));
    }
}
namespace Unity.Profiling {
    public enum ProfilerMarkerDataUnit : byte { Undefined, TimeNanoseconds, Bytes, Count, Percent, FrequencyHz }
    [Flags] public enum ProfilerCounterOptions : ushort { None = 0, FlushOnEndOfFrame = 2, ResetToZeroOnFlush = 4 }
    public readonly struct ProfilerCategory {
        readonly string _name;
        public ProfilerCategory(string name) { _name = name; }
        public static ProfilerCategory Scripts => new ProfilerCategory("Scripts");
        public static implicit operator ushort(ProfilerCategory c) => 0;
    }
    public readonly struct ProfilerMarker {
        public ProfilerMarker(ProfilerCategory category, string name) { }
        public ProfilerMarker(string name) { }
        public IntPtr Handle => IntPtr.Zero;
    }
}

namespace UnityEngine {
    // ---- attributes -------------------------------------------------------------------------
    public class PropertyAttribute : Attribute { public int order { get; set; } }
    public sealed class AddComponentMenu : Attribute { public AddComponentMenu(string menuName) { } public AddComponentMenu(string menuName, int order) { } }
    public sealed class DefaultExecutionOrder : Attribute { public int order { get; } public DefaultExecutionOrder(int order) { this.order = order; } }
    public sealed class DisallowMultipleComponent : Attribute { }
    public sealed class HeaderAttribute : PropertyAttribute { public HeaderAttribute(string header) { } }
    public sealed class HelpURLAttribute : Attribute { public HelpURLAttribute(string url) { } }
    public sealed class HideInCallstackAttribute : Attribute { }
    public sealed class HideInInspector : Attribute { }
    public sealed class RangeAttribute : PropertyAttribute { public RangeAttribute(float min, float max) { } }
    public sealed class SpaceAttribute : PropertyAttribute { public SpaceAttribute() { } public SpaceAttribute(float height) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class SerializeReference : Attribute { }
    public sealed class CreateAssetMenuAttribute : Attribute { public string menuName; public string fileName; public int order; }
    public sealed class UnityAPICompatibilityVersionAttribute : Attribute { public UnityAPICompatibilityVersionAttribute(string version, bool checkOnlyUnityVersion) { } }
    public enum RuntimeInitializeLoadType { AfterSceneLoad, BeforeSceneLoad, AfterAssembliesLoaded, BeforeSplashScreen, SubsystemRegistration }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute() { } public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { } }
    public interface ISerializationCallbackReceiver { void OnBeforeSerialize(); void OnAfterDeserialize(); }

    // ---- enums --------------------------------------------------------------------------------
    [Flags] public enum HideFlags { None = 0, HideInHierarchy = 1, HideInInspector = 2, DontSaveInEditor = 4, NotEditable = 8, DontSaveInBuild = 16, DontUnloadUnusedAsset = 32, DontSave = 52, HideAndDontSave = 61 }
    public enum RuntimePlatform { OSXEditor = 0, OSXPlayer = 1, WindowsPlayer = 2, WindowsEditor = 7, IPhonePlayer = 8, Android = 11, LinuxPlayer = 13, LinuxEditor = 16, WebGLPlayer = 17, Switch = 27, LinuxServer = 33 }
    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }
    [Flags] public enum RigidbodyConstraints { None = 0, FreezePositionX = 2, FreezePositionY = 4, FreezePositionZ = 8, FreezeRotationX = 16, FreezeRotationY = 32, FreezeRotationZ = 64, FreezePosition = 14, FreezeRotation = 112, FreezeAll = 126 }
    [Flags] public enum RigidbodyConstraints2D { None = 0, FreezePositionX = 1, FreezePositionY = 2, FreezeRotation = 4, FreezePosition = 3, FreezeAll = 7 }
    public enum RigidbodyInterpolation { None, Interpolate, Extrapolate }
    public enum RigidbodyInterpolation2D { None, Interpolate, Extrapolate }
    public enum SimulationMode2D { FixedUpdate, Update, Script }
    public enum AnimatorControllerParameterType { Float = 1, Int = 3, Bool = 4, Trigger = 9 }

    // ---- math ---------------------------------------------------------------------------------
    public static class Mathf {
        public const float PI = (float)Math.PI, Deg2Rad = PI / 180f, Rad2Deg = 180f / PI, Epsilon = 1.401298E-45f;
        public static float Abs(float f) => Math.Abs(f);
        public static bool Approximately(float a, float b) => Math.Abs(b - a) < Math.Max(1E-06f * Math.Max(Math.Abs(a), Math.Abs(b)), Epsilon * 8f);
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Repeat(float t, float length) => Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float current, float target) { float d = Repeat(target - current, 360f); if (d > 180f) d -= 360f; return d; }
        public static float LerpAngle(float a, float b, float t) => a + DeltaAngle(a, b) * Clamp01(t);
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Asin(float f) => (float)Math.Asin(f);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector2 : IEquatable<Vector2> {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public Vector2 normalized { get { float m = magnitude; return m > 1E-05f ? this / m : zero; } }
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = Mathf.Clamp01(t); return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);
        public bool Equals(Vector2 o) => x == o.x && y == o.y;
        public override bool Equals(object o) => o is Vector2 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(x, y);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3 : IEquatable<Vector3> {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; z = 0f; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 down => new Vector3(0, -1, 0);
        public static Vector3 left => new Vector3(-1, 0, 0);
        public static Vector3 right => new Vector3(1, 0, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 back => new Vector3(0, 0, -1);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized { get { float m = magnitude; return m > 1E-05f ? this / m : zero; } }
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        public static Vector3 Min(Vector3 a, Vector3 b) => new Vector3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        public static Vector3 MoveTowards(Vector3 c, Vector3 t, float maxDelta) { var d = t - c; float m = d.magnitude; return m <= maxDelta || m == 0f ? t : c + d / m * maxDelta; }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public bool Equals(Vector3 o) => x == o.x && y == o.y && z == o.z;
        public override bool Equals(object o) => o is Vector3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector4 : IEquatable<Vector4> {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 Lerp(Vector4 a, Vector4 b, float t) { t = Mathf.Clamp01(t); return new Vector4(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t); }
        public static implicit operator Vector3(Vector4 v) => new Vector3(v.x, v.y, v.z);
        public static implicit operator Vector4(Vector3 v) => new Vector4(v.x, v.y, v.z, 0f);
        public bool Equals(Vector4 o) => x == o.x && y == o.y && z == o.z && w == o.w;
        public override bool Equals(object o) => o is Vector4 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3Int : IEquatable<Vector3Int> {
        int m_X, m_Y, m_Z;
        public Vector3Int(int x, int y, int z) { m_X = x; m_Y = y; m_Z = z; }
        public int x { get => m_X; set => m_X = value; }
        public int y { get => m_Y; set => m_Y = value; }
        public int z { get => m_Z; set => m_Z = value; }
        public bool Equals(Vector3Int o) => m_X == o.m_X && m_Y == o.m_Y && m_Z == o.m_Z;
        public override bool Equals(object o) => o is Vector3Int v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(m_X, m_Y, m_Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Quaternion : IEquatable<Quaternion> {
        public float x, y, z, w;
        public const float kEpsilon = 1E-06f;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public Quaternion normalized => Normalize(this);
        public static Quaternion operator *(Quaternion l, Quaternion r) => new Quaternion(
            l.w * r.x + l.x * r.w + l.y * r.z - l.z * r.y,
            l.w * r.y + l.y * r.w + l.z * r.x - l.x * r.z,
            l.w * r.z + l.z * r.w + l.x * r.y - l.y * r.x,
            l.w * r.w - l.x * r.x - l.y * r.y - l.z * r.z);
        public static Vector3 operator *(Quaternion r, Vector3 p) {
            float x2 = r.x * 2f, y2 = r.y * 2f, z2 = r.z * 2f;
            float xx = r.x * x2, yy = r.y * y2, zz = r.z * z2, xy = r.x * y2, xz = r.x * z2, yz = r.y * z2, wx = r.w * x2, wy = r.w * y2, wz = r.w * z2;
            return new Vector3((1f - (yy + zz)) * p.x + (xy - wz) * p.y + (xz + wy) * p.z,
                               (xy + wz) * p.x + (1f - (xx + zz)) * p.y + (yz - wx) * p.z,
                               (xz - wy) * p.x + (yz + wx) * p.y + (1f - (xx + yy)) * p.z);
        }
        public static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        public static bool operator ==(Quaternion a, Quaternion b) => Dot(a, b) > 0.999999f;
        public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);
        public bool Equals(Quaternion o) => x == o.x && y == o.y && z == o.z && w == o.w;
        public override bool Equals(object o) => o is Quaternion q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public static Quaternion Inverse(Quaternion q) { float n = Dot(q, q); if (n == 0f) return identity; return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n); }
        public static Quaternion Normalize(Quaternion q) { float m = (float)Math.Sqrt(Dot(q, q)); return m < kEpsilon ? identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m); }
        public static float Angle(Quaternion a, Quaternion b) { float d = Math.Min(Math.Abs(Dot(a, b)), 1f); return d > 0.999999f ? 0f : (float)Math.Acos(d) * 2f * Mathf.Rad2Deg; }
        public static Quaternion AngleAxis(float angle, Vector3 axis) {
            axis = axis.normalized; float half = angle * Mathf.Deg2Rad * 0.5f; float s = (float)Math.Sin(half);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(half));
        }
        public static Quaternion Euler(float x, float y, float z) {
            // Unity order: Z, then X, then Y (extrinsic), i.e. q = Yaw(y) * Pitch(x) * Roll(z).
            return AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);
        }
        public static Quaternion Euler(Vector3 e) => Euler(e.x, e.y, e.z);
        public Vector3 eulerAngles { get {
            // Rotation matrix elements then Unity's decomposition (YXZ).
            var q = Normalize(this);
            float m11 = 1 - 2 * (q.y * q.y + q.z * q.z), m12 = 2 * (q.x * q.y - q.w * q.z), m13 = 2 * (q.x * q.z + q.w * q.y);
            float m21 = 2 * (q.x * q.y + q.w * q.z), m22 = 1 - 2 * (q.x * q.x + q.z * q.z), m23 = 2 * (q.y * q.z - q.w * q.x);
            float m31 = 2 * (q.x * q.z - q.w * q.y), m32 = 2 * (q.y * q.z + q.w * q.x), m33 = 1 - 2 * (q.x * q.x + q.y * q.y);
            float ex, ey, ez;
            if (m23 < 0.9999f) { if (m23 > -0.9999f) { ex = (float)Math.Asin(-m23); ey = (float)Math.Atan2(m13, m33); ez = (float)Math.Atan2(m21, m22); }
                                 else { ex = Mathf.PI / 2f; ey = (float)Math.Atan2(m12, m11); ez = 0f; } }
            else { ex = -Mathf.PI / 2f; ey = (float)Math.Atan2(-m12, m11); ez = 0f; }
            Vector3 r = new Vector3(ex, ey, ez) * Mathf.Rad2Deg;
            return new Vector3(Mathf.Repeat(r.x, 360f), Mathf.Repeat(r.y, 360f), Mathf.Repeat(r.z, 360f));
        } }
        public void ToAngleAxis(out float angle, out Vector3 axis) {
            var q = Normalize(this); angle = 2f * (float)Math.Acos(Math.Clamp(q.w, -1f, 1f)) * Mathf.Rad2Deg;
            float s = (float)Math.Sqrt(Math.Max(0f, 1f - q.w * q.w));
            axis = s < 0.0001f ? new Vector3(1, 0, 0) : new Vector3(q.x / s, q.y / s, q.z / s);
        }
        public static Quaternion Lerp(Quaternion a, Quaternion b, float t) {
            t = Mathf.Clamp01(t); if (Dot(a, b) < 0f) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
            return Normalize(new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
        }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t) {
            t = Mathf.Clamp01(t); float d = Dot(a, b);
            if (d < 0f) { b = new Quaternion(-b.x, -b.y, -b.z, -b.w); d = -d; }
            if (d > 0.9995f) return Lerp(a, b, t);
            float th = (float)Math.Acos(d), s = (float)Math.Sin(th), wa = (float)Math.Sin((1 - t) * th) / s, wb = (float)Math.Sin(t * th) / s;
            return new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        }
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta) {
            float a = Angle(from, to); return a == 0f ? to : Slerp(from, to, Math.Min(1f, maxDegreesDelta / a));
        }
        public static Quaternion LookRotation(Vector3 forward, Vector3 up) {
            forward = forward.normalized; if (forward.sqrMagnitude < 1e-8f) return identity;
            Vector3 right = Vector3.Cross(up, forward).normalized; if (right.sqrMagnitude < 1e-8f) right = Vector3.right;
            Vector3 u = Vector3.Cross(forward, right);
            float m00 = right.x, m01 = u.x, m02 = forward.x, m10 = right.y, m11 = u.y, m12 = forward.y, m20 = right.z, m21 = u.z, m22 = forward.z;
            float tr = m00 + m11 + m22; Quaternion q;
            if (tr > 0f) { float s = (float)Math.Sqrt(tr + 1f) * 2f; q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
            else if (m00 > m11 && m00 > m22) { float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f; q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
            else if (m11 > m22) { float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f; q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
            else { float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f; q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
            return Normalize(q);
        }
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix4x4 {
        public float m00, m10, m20, m30, m01, m11, m21, m31, m02, m12, m22, m32, m03, m13, m23, m33;
        public static Matrix4x4 identity { get { var m = default(Matrix4x4); m.m00 = m.m11 = m.m22 = m.m33 = 1f; return m; } }
        public Vector4 GetColumn(int i) => i switch { 0 => new Vector4(m00, m10, m20, m30), 1 => new Vector4(m01, m11, m21, m31), 2 => new Vector4(m02, m12, m22, m32), _ => new Vector4(m03, m13, m23, m33) };
        public Vector3 MultiplyPoint3x4(Vector3 p) => new Vector3(m00 * p.x + m01 * p.y + m02 * p.z + m03, m10 * p.x + m11 * p.y + m12 * p.z + m13, m20 * p.x + m21 * p.y + m22 * p.z + m23);
        public Vector3 MultiplyPoint(Vector3 p) { var r = MultiplyPoint3x4(p); float w = m30 * p.x + m31 * p.y + m32 * p.z + m33; return w != 0f && w != 1f ? r / w : r; }
        public Vector3 MultiplyVector(Vector3 v) => new Vector3(m00 * v.x + m01 * v.y + m02 * v.z, m10 * v.x + m11 * v.y + m12 * v.z, m20 * v.x + m21 * v.y + m22 * v.z);
        public Quaternion rotation => Quaternion.LookRotation(new Vector3(m02, m12, m22), new Vector3(m01, m11, m21));
        public static Matrix4x4 TRS(Vector3 pos, Quaternion q, Vector3 s) {
            var m = identity;
            Vector3 c0 = q * new Vector3(s.x, 0, 0), c1 = q * new Vector3(0, s.y, 0), c2 = q * new Vector3(0, 0, s.z);
            m.m00 = c0.x; m.m10 = c0.y; m.m20 = c0.z; m.m01 = c1.x; m.m11 = c1.y; m.m21 = c1.z; m.m02 = c2.x; m.m12 = c2.y; m.m22 = c2.z;
            m.m03 = pos.x; m.m13 = pos.y; m.m23 = pos.z; return m;
        }
        public Matrix4x4 inverse { get {
            var a = new System.Numerics.Matrix4x4(m00, m10, m20, m30, m01, m11, m21, m31, m02, m12, m22, m32, m03, m13, m23, m33);
            System.Numerics.Matrix4x4.Invert(a, out var r);
            var m = default(Matrix4x4);
            m.m00 = r.M11; m.m10 = r.M12; m.m20 = r.M13; m.m30 = r.M14; m.m01 = r.M21; m.m11 = r.M22; m.m21 = r.M23; m.m31 = r.M24;
            m.m02 = r.M31; m.m12 = r.M32; m.m22 = r.M33; m.m32 = r.M34; m.m03 = r.M41; m.m13 = r.M42; m.m23 = r.M43; m.m33 = r.M44; return m;
        } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Bounds {
        Vector3 m_Center, m_Extents;
        public Bounds(Vector3 center, Vector3 size) { m_Center = center; m_Extents = size * 0.5f; }
        public Vector3 center { get => m_Center; set => m_Center = value; }
        public Vector3 extents { get => m_Extents; set => m_Extents = value; }
        public Vector3 size { get => m_Extents * 2f; set => m_Extents = value * 0.5f; }
        public Vector3 min { get => m_Center - m_Extents; set => SetMinMax(value, max); }
        public Vector3 max { get => m_Center + m_Extents; set => SetMinMax(min, value); }
        public void SetMinMax(Vector3 mn, Vector3 mx) { m_Extents = (mx - mn) * 0.5f; m_Center = mn + m_Extents; }
        public void Encapsulate(Vector3 p) => SetMinMax(Vector3.Min(min, p), Vector3.Max(max, p));
        public void Encapsulate(Bounds b) { Encapsulate(b.min); Encapsulate(b.max); }
        public void Expand(Vector3 amount) => m_Extents += amount * 0.5f;
        public bool Contains(Vector3 p) { var mn = min; var mx = max; return p.x >= mn.x && p.x <= mx.x && p.y >= mn.y && p.y <= mx.y && p.z >= mn.z && p.z <= mx.z; }
        public bool Intersects(Bounds b) { var mn = min; var mx = max; var bn = b.min; var bx = b.max; return mn.x <= bx.x && mx.x >= bn.x && mn.y <= bx.y && mx.y >= bn.y && mn.z <= bx.z && mx.z >= bn.z; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Color {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color red => new Color(1, 0, 0); public static Color green => new Color(0, 1, 0); public static Color blue => new Color(0, 0, 1);
        public static Color white => new Color(1, 1, 1); public static Color yellow => new Color(1, 0.92f, 0.016f); public static Color gray => new Color(0.5f, 0.5f, 0.5f);
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Color32 {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        static byte C(float f) => (byte)Math.Clamp((int)Math.Round(f * 255f), 0, 255);
        public static implicit operator Color32(Color c) => new Color32(C(c.r), C(c.g), C(c.b), C(c.a));
        public static implicit operator Color(Color32 c) => new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct LayerMask { public int value; public static implicit operator int(LayerMask m) => m.value; public static implicit operator LayerMask(int v) => new LayerMask { value = v }; }

    // ---- objects ------------------------------------------------------------------------------
    public partial class Object {
        static int _nextId = 1;
        readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);
        internal bool _destroyed;
        public string name { get; set; } = "";
        public HideFlags hideFlags { get; set; }
        public int GetInstanceID() => _id;
        public static implicit operator bool(Object o) => o is not null && !o._destroyed;
        public static bool operator ==(Object a, Object b) { bool an = a is null || a._destroyed, bn = b is null || b._destroyed; return an && bn || ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object o) => o is Object u ? this == u : o is null && _destroyed;
        public override int GetHashCode() => _id;
        public static void Destroy(Object o) { if (o is not null) o._destroyed = true; }
        public static void Destroy(Object o, float t) => Destroy(o);
        public static void DontDestroyOnLoad(Object o) { }
        public static T Instantiate<T>(T original) where T : Object => throw new NotSupportedException("Object.Instantiate (headless stub)");
        public static T Instantiate<T>(T original, Vector3 p, Quaternion r) where T : Object => throw new NotSupportedException("Object.Instantiate (headless stub)");
        public override string ToString() => name;
    }
    public class ScriptableObject : Object { }
    public partial class GameObject : Object {
        readonly List<Component> _components = new();
        public GameObject() { }
        public GameObject(string name) { this.name = name; }
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf;
        public int layer { get; set; }
        public SceneManagement.Scene scene => default;
        public Transform transform { get; }
        public void SetActive(bool v) => activeSelf = v;
        // Unity constrains this to Component only. Requiring new() as well rejects any component
        // without a public parameterless constructor at the CALL SITE inside Fusion, which fails as
        // "type argument violates the constraint of type parameter" when its runner adds its own
        // helpers — so construct through Activator instead, exactly as AddComponent(Type) does.
        public T AddComponent<T>() where T : Component { var c = (T)Activator.CreateInstance(typeof(T)); c.gameObject = this; _components.Add(c); return c; }
        public Component AddComponent(Type t) { var c = (Component)Activator.CreateInstance(t); c.gameObject = this; _components.Add(c); return c; }
        public T GetComponent<T>() { foreach (var c in _components) if (c is T t) return t; return default; }
        public T GetComponentInChildren<T>() => GetComponent<T>();
        public T[] GetComponentsInChildren<T>() => GetComponentsInChildren<T>(false);
        public T[] GetComponentsInChildren<T>(bool includeInactive) { var l = new List<T>(); foreach (var c in _components) if (c is T t) l.Add(t); return l.ToArray(); }
        public bool TryGetComponent<T>(out T r) { r = GetComponent<T>(); return r != null; }
    }
    public partial class Component : Object {
        public GameObject gameObject { get; internal set; }
        public Transform transform => gameObject?.transform;
        public T GetComponent<T>() => gameObject == null ? default : gameObject.GetComponent<T>();
        public void GetComponents<T>(List<T> results) { results.Clear(); if (gameObject != null) results.AddRange(gameObject.GetComponentsInChildren<T>()); }
        public T[] GetComponentsInChildren<T>() => gameObject == null ? Array.Empty<T>() : gameObject.GetComponentsInChildren<T>();
        public bool TryGetComponent<T>(out T r) { r = GetComponent<T>(); return r != null; }
        public bool TryGetComponent(Type t, out Component r) { r = null; return false; }
    }
    public class Behaviour : Component { public bool enabled { get; set; } = true; }
    public class MonoBehaviour : Behaviour { public Coroutine StartCoroutine(IEnumerator routine) => new Coroutine(); }
    public sealed class Coroutine { }
    public class AsyncOperation { public bool isDone => true; public event Action<AsyncOperation> completed; }
    public sealed class WaitUntil { public WaitUntil(Func<bool> predicate) { } }
    public class Transform : Component {
        public Vector3 position { get; set; } public Quaternion rotation { get; set; } = Quaternion.identity;
        public Vector3 localPosition { get; set; } public Quaternion localRotation { get; set; } = Quaternion.identity; public Vector3 localScale { get; set; } = Vector3.one;
        public Transform parent { get; set; } public int childCount => 0;
        public Transform GetChild(int i) => throw new IndexOutOfRangeException();
        public void SetParent(Transform p) => parent = p;
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
        public void GetPositionAndRotation(out Vector3 p, out Quaternion r) { p = position; r = rotation; }
        public Vector3 TransformPoint(Vector3 p) => rotation * p + position;
    }
    public class Collider : Component { }
    public class Collider2D : Component { }
    public class Rigidbody : Component {
        public Vector3 position { get; set; } public Quaternion rotation { get; set; } = Quaternion.identity;
        public Vector3 velocity { get; set; } public Vector3 angularVelocity { get; set; }
        public float mass { get; set; } = 1f; public float drag { get; set; } public float angularDrag { get; set; }
        public bool isKinematic { get; set; } public Vector3 inertiaTensor { get; set; } = Vector3.one;
        public RigidbodyConstraints constraints { get; set; } public RigidbodyInterpolation interpolation { get; set; }
        public void AddForce(Vector3 f) { } public void AddTorque(Vector3 t) { } public bool IsSleeping() => false; public void Sleep() { } public void WakeUp() { }
    }
    public class Rigidbody2D : Component {
        public Vector2 position { get; set; } public float rotation { get; set; } public Vector2 velocity { get; set; } public float angularVelocity { get; set; }
        public float mass { get; set; } = 1f; public float drag { get; set; } public float angularDrag { get; set; } public float inertia { get; set; }
        public bool isKinematic { get; set; } public RigidbodyConstraints2D constraints { get; set; } public RigidbodyInterpolation2D interpolation { get; set; }
        public void AddForce(Vector2 f) { } public void AddTorque(float t) { } public bool IsSleeping() => false; public void Sleep() { } public void WakeUp() { }
    }
    public struct AnimatorStateInfo { public int fullPathHash => 0; public float normalizedTime => 0f; }
    public class AnimatorControllerParameter { public int nameHash => 0; public AnimatorControllerParameterType type => AnimatorControllerParameterType.Float; }
    public class Animator : Behaviour {
        public int layerCount => 0; public int parameterCount => 0; public AnimatorControllerParameter[] parameters => Array.Empty<AnimatorControllerParameter>();
        public static int StringToHash(string s) => s.GetHashCode();
        public bool GetBool(int id) => false; public float GetFloat(int id) => 0f; public int GetInteger(int id) => 0; public float GetLayerWeight(int l) => 0f;
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int l) => default; public bool IsInTransition(int l) => false; public bool IsParameterControlledByCurve(int id) => false;
        public void Play(int h, int l, float t) { } public void SetBool(int id, bool v) { } public void SetFloat(int id, float v) { } public void SetInteger(int id, int v) { }
        public void SetLayerWeight(int l, float w) { } public void SetTrigger(int id) { } public void SetTrigger(string n) { }
    }
    public struct ContactPoint { public Vector3 normal => Vector3.up; }
    public struct ContactPoint2D { public Vector2 normal => new Vector2(0, 1); }
    public class Collision { public int contactCount => 0; public Vector3 relativeVelocity => Vector3.zero; public ContactPoint GetContact(int i) => default; }
    public class Collision2D { public int contactCount => 0; public Vector2 relativeVelocity => Vector2.zero; public ContactPoint2D GetContact(int i) => default; }
    public struct RaycastHit { public Collider collider => null; public float distance => 0f; public Vector3 normal => Vector3.up; public Vector3 point => Vector3.zero; }
    public struct RaycastHit2D { public Collider2D collider => null; public float distance => 0f; public Vector2 normal => new Vector2(0, 1); public Vector2 point => Vector2.zero; }
    public struct PhysicsScene {
        public bool IsValid() => false;
        public static bool operator ==(PhysicsScene a, PhysicsScene b) => true; public static bool operator !=(PhysicsScene a, PhysicsScene b) => false;
        public bool Raycast(Vector3 o, Vector3 d, out RaycastHit hit, float md, int mask, QueryTriggerInteraction q) { hit = default; return false; }
        public int Raycast(Vector3 o, Vector3 d, RaycastHit[] r, float md, int mask, QueryTriggerInteraction q) => 0;
        public int OverlapBox(Vector3 c, Vector3 h, Collider[] r, Quaternion o, int mask, QueryTriggerInteraction q) => 0;
        public int OverlapSphere(Vector3 c, float rad, Collider[] r, int mask, QueryTriggerInteraction q) => 0;
        public void Simulate(float step) { }
    }
    public struct PhysicsScene2D {
        public bool IsValid() => false;
        public static bool operator ==(PhysicsScene2D a, PhysicsScene2D b) => true; public static bool operator !=(PhysicsScene2D a, PhysicsScene2D b) => false;
        public bool Simulate(float step) => true;
        public int OverlapBox(Vector2 p, Vector2 s, float a, Collider2D[] r, int mask) => 0;
        public int OverlapCircle(Vector2 p, float rad, Collider2D[] r, int mask) => 0;
        public int Raycast(Vector2 o, Vector2 d, float dist, RaycastHit2D[] r, int mask) => 0;
        public RaycastHit2D Raycast(Vector2 o, Vector2 d, float dist, int mask) => default;
    }
    public static class Physics { public static bool autoSimulation => true; public static PhysicsScene defaultPhysicsScene => default; public static Vector3 gravity => new Vector3(0, -9.81f, 0); }
    public static class Physics2D { public static PhysicsScene2D defaultPhysicsScene => default; public static SimulationMode2D simulationMode => SimulationMode2D.FixedUpdate; public static Vector2 gravity => new Vector2(0, -9.81f); }
    public static class Gizmos { public static Color color { get; set; } public static Matrix4x4 matrix { get; set; } public static void DrawLine(Vector3 a, Vector3 b) { } public static void DrawRay(Vector3 a, Vector3 d) { } public static void DrawWireCube(Vector3 c, Vector3 s) { } public static void DrawWireSphere(Vector3 c, float r) { } }
    public static class Random { static readonly System.Random _r = new(); public static float value => (float)_r.NextDouble(); }

    public static class Time {
        // Headless: the host advances these explicitly before each simulation update.
        public static float deltaTime { get; set; } = 1f / 60f;
        public static float unscaledDeltaTime { get; set; } = 1f / 60f;
        public static float fixedDeltaTime { get; set; } = 1f / 60f;
    }
    public static partial class Application {
        public static bool isBatchMode => true; public static bool isEditor => false; public static bool isPlaying => true;
        public static string persistentDataPath => System.IO.Path.GetTempPath();
        public static RuntimePlatform platform => RuntimePlatform.LinuxServer;
    }
    public static class Debug {
        public static Action<string> Sink = s => Console.WriteLine(s);
        public static void Log(object m) => Sink("[Unity] " + m);
        public static void Log(object m, Object ctx) => Log(m);
        public static void LogWarning(object m) => Sink("[Unity][warn] " + m);
        public static void LogWarning(object m, Object ctx) => LogWarning(m);
        public static void LogError(object m) => Sink("[Unity][error] " + m);
        public static void LogError(object m, Object ctx) => LogError(m);
        public static void LogException(Exception e) => Sink("[Unity][exception] " + e);
        public static void LogException(Exception e, Object ctx) => LogException(e);
    }
    public static class JsonUtility {
        // Unity's JsonUtility serializes public + [SerializeField] fields by name; System.Text.Json with
        // IncludeFields covers the public-field case, which is all NetworkProjectConfig uses.
        static readonly JsonSerializerOptions Opts = new() { IncludeFields = true, WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverterOff() } };
        sealed class JsonStringEnumConverterOff : JsonConverterFactory { public override bool CanConvert(Type t) => false; public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) => null; }
        public static string ToJson(object obj) => JsonSerializer.Serialize(obj, obj.GetType(), Opts);
        public static string ToJson(object obj, bool prettyPrint) => JsonSerializer.Serialize(obj, obj.GetType(), prettyPrint ? new JsonSerializerOptions(Opts) { WriteIndented = true } : Opts);
        /// <summary>
        /// Reads JSON the way Unity's JsonUtility does, which System.Text.Json cannot be configured
        /// into: FIELDS only (public, or private with [SerializeField]), properties ignored, unknown
        /// keys ignored, missing keys left at their default, enums as numbers, and no constructor
        /// requirement. Needed to load a real client's NetworkProjectConfig — which it sends us as
        /// JSON in its NetworkConfigSync — into a Fusion runner: System.Text.Json threw
        /// NotSupportedException on it, since the SDK's config is a graph of field-only types.
        /// </summary>
        public static T FromJson<T>(string json)
        {
            using var doc = JsonDocument.Parse(Sanitize(json));
            return (T)ReadValue(doc.RootElement, typeof(T));
        }

        /// <summary>
        /// Unity writes non-finite floats as bare <c>Infinity</c>/<c>-Infinity</c>/<c>NaN</c>, which no
        /// strict JSON parser accepts — a real Moving Out 2 config carries `"_inverse":Infinity`. Quote
        /// them so they survive as strings; the number readers below take those back.
        /// </summary>
        private static string Sanitize(string json) => NonFinite.Replace(json, "$1\"$2\"");

        private static readonly System.Text.RegularExpressions.Regex NonFinite =
            new(@"([:\[,]\s*)(-?Infinity|NaN)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static object ReadValue(JsonElement e, Type t)
        {
            if (t == typeof(string)) return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (t.IsEnum) return e.ValueKind == JsonValueKind.Number ? Enum.ToObject(t, e.GetInt64()) : Enum.Parse(t, e.GetString(), true);

            var under = Nullable.GetUnderlyingType(t);
            if (under != null) return e.ValueKind == JsonValueKind.Null ? null : ReadValue(e, under);

            if (t == typeof(bool)) return e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt32() != 0);
            if (t == typeof(byte)) return e.GetByte();
            if (t == typeof(sbyte)) return e.GetSByte();
            if (t == typeof(short)) return e.GetInt16();
            if (t == typeof(ushort)) return e.GetUInt16();
            if (t == typeof(int)) return e.GetInt32();
            if (t == typeof(uint)) return e.GetUInt32();
            if (t == typeof(long)) return e.GetInt64();
            if (t == typeof(ulong)) return e.GetUInt64();
            if (t == typeof(float)) return (float)AsDouble(e);
            if (t == typeof(double)) return AsDouble(e);

            if (t.IsArray)
            {
                var element = t.GetElementType();
                if (e.ValueKind != JsonValueKind.Array) return Array.CreateInstance(element, 0);
                var array = Array.CreateInstance(element, e.GetArrayLength());
                int i = 0;
                foreach (var item in e.EnumerateArray()) array.SetValue(ReadValue(item, element), i++);
                return array;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var element = t.GetGenericArguments()[0];
                var list = (System.Collections.IList)Activator.CreateInstance(t);
                if (e.ValueKind == JsonValueKind.Array)
                    foreach (var item in e.EnumerateArray()) list.Add(ReadValue(item, element));
                return list;
            }

            // A field-only object graph: make it without running a constructor, then fill what is named.
            var instance = t.IsValueType ? Activator.CreateInstance(t) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            if (e.ValueKind != JsonValueKind.Object) return instance;

            foreach (var field in Fields(t))
            {
                if (!e.TryGetProperty(field.Name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                try { field.SetValue(instance, ReadValue(value, field.FieldType)); }
                catch (Exception) { /* a shape Unity would skip too; leave the default */ }
            }
            return instance;
        }

        /// <summary>A number, or one of the quoted non-finite literals Sanitize preserved.</summary>
        private static double AsDouble(JsonElement e) => e.ValueKind == JsonValueKind.String
            ? double.Parse(e.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : e.GetDouble();

        private static IEnumerable<System.Reflection.FieldInfo> Fields(Type t)
        {
            const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            for (var type = t; type != null && type != typeof(object); type = type.BaseType)
                foreach (var f in type.GetFields(Flags))
                    if (f.IsPublic || f.GetCustomAttributes(typeof(SerializeField), inherit: true).Length > 0)
                        yield return f;
        }
    }
}

namespace UnityEngine.Events {
    public class UnityEvent<T0> { readonly List<Action<T0>> _l = new(); public void AddListener(Action<T0> a) => _l.Add(a); public void RemoveListener(Action<T0> a) => _l.Remove(a); public void Invoke(T0 a) { foreach (var x in _l.ToArray()) x(a); } }
    public class UnityEvent<T0, T1> { readonly List<Action<T0, T1>> _l = new(); public void AddListener(Action<T0, T1> a) => _l.Add(a); public void RemoveListener(Action<T0, T1> a) => _l.Remove(a); public void Invoke(T0 a, T1 b) { foreach (var x in _l.ToArray()) x(a, b); } }
    public class UnityEvent<T0, T1, T2> { readonly List<Action<T0, T1, T2>> _l = new(); public void AddListener(Action<T0, T1, T2> a) => _l.Add(a); public void RemoveListener(Action<T0, T1, T2> a) => _l.Remove(a); public void Invoke(T0 a, T1 b, T2 c) { foreach (var x in _l.ToArray()) x(a, b, c); } }
    public class UnityEvent<T0, T1, T2, T3> { readonly List<Action<T0, T1, T2, T3>> _l = new(); public void AddListener(Action<T0, T1, T2, T3> a) => _l.Add(a); public void RemoveListener(Action<T0, T1, T2, T3> a) => _l.Remove(a); public void Invoke(T0 a, T1 b, T2 c, T3 d) { foreach (var x in _l.ToArray()) x(a, b, c, d); } }
}
namespace UnityEngine.Profiling { public static class Profiler { public static void BeginSample(string name) { } public static void BeginSample(string name, Object targetObject) { } public static void EndSample() { } } }
namespace UnityEngine.Scripting { public sealed class PreserveAttribute : Attribute { } }
namespace UnityEngine.Serialization { public sealed class FormerlySerializedAsAttribute : Attribute { public FormerlySerializedAsAttribute(string oldName) { } } }
namespace UnityEngine.LowLevel {
    public struct PlayerLoopSystem {
        public delegate void UpdateFunction();
        public Type type; public PlayerLoopSystem[] subSystemList; public UpdateFunction updateDelegate; public IntPtr updateFunction, loopConditionFunction;
    }
    public static class PlayerLoop {
        static PlayerLoopSystem _current = new PlayerLoopSystem { subSystemList = Array.Empty<PlayerLoopSystem>() };
        public static PlayerLoopSystem GetCurrentPlayerLoop() => _current;
        public static void SetPlayerLoop(PlayerLoopSystem loop) => _current = loop;
    }
}
namespace UnityEngine.PlayerLoop { public struct Update { public struct ScriptRunBehaviourUpdate { } } }
namespace UnityEngine.SceneManagement {
    public enum LoadSceneMode { Single, Additive }
    public enum LocalPhysicsMode { None = 0, Physics2D = 1, Physics3D = 2 }
    public struct LoadSceneParameters {
        public LoadSceneParameters(LoadSceneMode mode, LocalPhysicsMode physics) { loadSceneMode = mode; localPhysicsMode = physics; }
        public LoadSceneMode loadSceneMode { get; set; } public LocalPhysicsMode localPhysicsMode { get; set; }
    }
    public partial struct Scene {
        public bool IsValid() => false;
        public GameObject[] GetRootGameObjects() => Array.Empty<GameObject>();
        public static bool operator ==(Scene a, Scene b) => true; public static bool operator !=(Scene a, Scene b) => false;
    }
    // partial so a harness can add the surface a different Fusion version wants without touching this
    // file: scratch/mo2/unitystub-112 compiles this source plus its own extras for Fusion 1.1.x.
    public static partial class SceneManager { public static Scene GetActiveScene() => default; public static bool SetActiveScene(Scene s) => true; public static void MoveGameObjectToScene(GameObject g, Scene s) { } }
}
