using System.Runtime.InteropServices;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace RampFix;

[StructLayout(LayoutKind.Explicit)]
public unsafe ref struct CGameTrace
{
    [FieldOffset(0)]
    public PhysicsSurfaceProperties* SurfaceProp;

    [FieldOffset(8)]
    public nint Entity;

    [FieldOffset(16)]
    public HitBoxData* HitBoxData;

    [FieldOffset(24)]
    public nint PhysicsBody;

    [FieldOffset(32)]
    public nint PhysicsShape;

    [FieldOffset(40)]
    public uint Contents;

    [FieldOffset(80)]
    public RnCollisionAttr ShapeAttributes;

    [FieldOffset(120)]
    public Vector StartPosition;

    [FieldOffset(132)]
    public Vector EndPosition;

    [FieldOffset(144)]
    public Vector PlaneNormal;

    [FieldOffset(156)]
    public Vector HitPoint;

    [FieldOffset(168)]
    public float HitOffset;

    [FieldOffset(172)]
    public float Fraction;

    [FieldOffset(180)]
    public float Triangle;

    [FieldOffset(184)]
    public short HitBoxBoneIndex;

    [FieldOffset(186)]
    public TraceRayType RayType;

    [FieldOffset(187)]
    public bool StartInSolid;

    [FieldOffset(188)]
    public bool ExactHitPoint;

    public bool DidHit()
        => Fraction < 1.0f || StartInSolid;
}
