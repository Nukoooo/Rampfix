using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Hooks;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

[assembly: DisableRuntimeMarshalling]

namespace RampFix;

public class RampFix : IModSharpModule, IGameListener
{
    string IModSharpModule.DisplayName   => "RampFix";
    string IModSharpModule.DisplayAuthor => "zer0.k, ported by Nukoooo";

    private readonly IDetourHook      _tryPlayerMoveHook;
    private readonly IDetourHook      _categorizePositionHook;
    private readonly ILogger<RampFix> _logger;

    private static unsafe delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>
        CCSPlayer_MovementService_TryPlayerMoveOriginal;
    private static unsafe delegate* unmanaged<nint, MoveData*, bool, void> CCSPlayer_MovementService_CategorizePositionOrigin;

    private static unsafe delegate* unmanaged[SuppressGCTransition]<IntPtr, TraceShapeRay*, Vector*, Vector*,
        CTraceFilter*, CGameTrace*, bool> TraceShape;

    private static nint g_pPhysicsQuery = 0;

    private readonly IGameData    _gameData;
    private readonly IHookManager _hookManager;

    private static IModSharp _modSharp;

    private static nint CTraceFilterPlayerMovementCS_vtable;

    private static int CPlayerPawnComponent_ChainEntityOffset;
    private static int CBaseEntity_m_lifestateOffset;
    private static int CBaseEntity_m_MoveTypeOffset;
    private static int CBaseEntity_m_hGroundEntityOffset;
    private static int CBaseEntity_m_pCollisionOffset;
    private static int CCollisionProperty_m_collisionAttributeOffset;
    private static int VPhysicsCollisionAttribute_t_m_nInteractsWithOffset;
    private static int VPhysicsCollisionAttribute_t_m_nHierarchyIdOffset;
    private static int CBasePlayerPawn_m_hControllerOffset;
    private static int CCSPlayer_MovementServices_m_bDuckedOffset;

    private const  int  CGlobalVars_FrametimeOffset = 0x34;
    private static nint g_pGlobalVars               = 0;

    public RampFix(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var factory = sharedSystem.GetLoggerFactory();
        _logger = factory.CreateLogger<RampFix>();

        _hookManager = sharedSystem.GetHookManager();

        _modSharp = sharedSystem.GetModSharp();
        _gameData = _modSharp.GetGameData();

        CTraceFilterPlayerMovementCS_vtable = sharedSystem.GetLibraryModuleManager()
                                                          .Server
                                                          .GetVirtualTableByName("CTraceFilterPlayerMovementCS");

        _tryPlayerMoveHook      = _hookManager.CreateDetourHook();
        _categorizePositionHook = _hookManager.CreateDetourHook();

        var schemaManager = sharedSystem.GetSchemaManager();

        CPlayerPawnComponent_ChainEntityOffset = schemaManager.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBaseEntity_m_lifestateOffset          = schemaManager.GetNetVarOffset("CBaseEntity", "m_lifeState");
        CBaseEntity_m_MoveTypeOffset           = schemaManager.GetNetVarOffset("CBaseEntity", "m_MoveType");
        CBaseEntity_m_hGroundEntityOffset      = schemaManager.GetNetVarOffset("CBaseEntity", "m_hGroundEntity");
        CBaseEntity_m_pCollisionOffset         = schemaManager.GetNetVarOffset("CBaseEntity", "m_pCollision");
        CBasePlayerPawn_m_hControllerOffset    = schemaManager.GetNetVarOffset("CBasePlayerPawn", "m_hController");

        CCollisionProperty_m_collisionAttributeOffset
            = schemaManager.GetNetVarOffset("CCollisionProperty", "m_collisionAttribute");

        VPhysicsCollisionAttribute_t_m_nInteractsWithOffset
            = schemaManager.GetNetVarOffset("VPhysicsCollisionAttribute_t", "m_nInteractsWith");

        VPhysicsCollisionAttribute_t_m_nHierarchyIdOffset
            = schemaManager.GetNetVarOffset("VPhysicsCollisionAttribute_t", "m_nHierarchyId");

        CCSPlayer_MovementServices_m_bDuckedOffset
            = schemaManager.GetNetVarOffset("CCSPlayer_MovementServices", "m_bDucked");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe nint CPlayerPawnComponent_GetOuter(nint component)
        => *(nint*) (component + CPlayerPawnComponent_ChainEntityOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool CCSPlayer_MovementServices_IsDucked(nint service)
        => *(bool*) (service + CCSPlayer_MovementServices_m_bDuckedOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte CBasePlayerPawn_GetPlayerSlot(nint pawn)
    {
        var handle = *(uint*) (pawn + CBasePlayerPawn_m_hControllerOffset);

        if (handle == uint.MaxValue)
            return byte.MaxValue;

        return (byte) ((handle & 0x7FFF) - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool CBaseEntity_IsAlive(nint entity)
        => *(LifeState*) (entity + CBaseEntity_m_lifestateOffset) == LifeState.Alive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe MoveType CBaseEntity_GetMovetype(nint entity)
        => *(MoveType*) (entity + CBaseEntity_m_MoveTypeOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool CBaseEntity_IsOnGround(nint entity)
        => *(uint*) (entity + CBaseEntity_m_hGroundEntityOffset) != uint.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe InteractionLayers CBaseEntity_GetInteractsWithLayers(nint entity)
    {
        var collision = *(nint*) (entity + CBaseEntity_m_pCollisionOffset);

        return *(InteractionLayers*)
            (collision
             + CCollisionProperty_m_collisionAttributeOffset
             + VPhysicsCollisionAttribute_t_m_nInteractsWithOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ushort CBaseEntity_GetHierarchyId(nint entity)
    {
        var collision = *(nint*) (entity + CBaseEntity_m_pCollisionOffset);

        return *(ushort*)
            (collision
             + CCollisionProperty_m_collisionAttributeOffset
             + VPhysicsCollisionAttribute_t_m_nHierarchyIdOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint CBaseEntity_GetRefHandle(nint entity)
    {
        var identity = *(nint*) (entity + 0x10);

        if (identity == nint.Zero)
            return uint.MaxValue;

        var handle = *(uint*) (identity + 0x10);
        var flags  = *(uint*) (entity   + 0x30);

        var lo     = handle != 0xFFFFFFFF ? handle & 0x7FFF : 0x7FFF;
        var hi     = (handle >> 15) - (flags & 0x1) << 15;
        var result = lo | hi;

        return result;
    }

    public unsafe bool Init()
    {
        _gameData.Register("rampfix.games");

        if (!_modSharp.GetGameData().GetAddress("CGamePhysicsQueryInterface::TraceShape", out var address))
        {
            _logger.LogInformation("Failed to get address for CGamePhysicsQueryInterface::TraceShape");

            return false;
        }

        TraceShape = (delegate* unmanaged[SuppressGCTransition]<IntPtr, TraceShapeRay*, Vector*, Vector*, CTraceFilter*,
            CGameTrace*, bool>) address;

        if (!_modSharp.GetGameData().GetAddress("g_pPhysicsQuery", out address))
        {
            _logger.LogInformation("Failed to get address for g_pPhysicsQuery");

            return false;
        }

        g_pPhysicsQuery = address;

        _tryPlayerMoveHook.Prepare("CCSPlayer_MovementService::TryPlayerMove",
                                   (nint) (delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>)
                                   (&hk_CCSPlayer_MovementService_TryPlayerMove));

        _categorizePositionHook.Prepare("CCSPlayer_MovementService::CategorizePosition",
                                        (nint) (delegate* unmanaged<nint, MoveData*, bool, void>)
                                        (&hk_CCSPlayer_MovementService_CategorizePosition));

        if (_tryPlayerMoveHook.Install())
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal
                = (delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>) _tryPlayerMoveHook.Trampoline;
        }

        if (_categorizePositionHook.Install())
        {
            CCSPlayer_MovementService_CategorizePositionOrigin
                = (delegate* unmanaged<nint, MoveData*, bool, void>) _categorizePositionHook.Trampoline;

            _hookManager.PlayerProcessMovePre.InstallForward(OnPreProcessMovement);
            _hookManager.PlayerProcessMovePost.InstallForward(OnPostProcessMovement);

            _modSharp.InstallGameListener(this);

            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void TracePlayerBBox(Vector*        start,
                                               Vector*        end,
                                               TraceShapeRay* ray,
                                               CTraceFilter*  filter,
                                               CGameTrace*    trace)
    {
        TraceShape(g_pPhysicsQuery, ray, start, end, filter, trace);
    }

    public void OnServerInit()
    {
        g_pGlobalVars = _modSharp.GetGlobals().GetAbsPtr();
    }

    public void OnGamePreShutdown()
    {
        g_pGlobalVars = 0;
    }

    public void Shutdown()
    {
        try
        {
            _hookManager.PlayerProcessMovePre.RemoveForward(OnPreProcessMovement);
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _hookManager.PlayerProcessMovePost.RemoveForward(OnPostProcessMovement);
        }
        catch (Exception)
        {
            // ignored
        }

        _tryPlayerMoveHook.Uninstall();
        _categorizePositionHook.Uninstall();

        _tryPlayerMoveHook.Dispose();
        _categorizePositionHook.Dispose();

        _modSharp.RemoveGameListener(this);
    }

    private static readonly Vector[] LastValidPlaneNormal = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] TpmOrigin            = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] TpmVelocity          = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   OverridenTpm         = new bool[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   DidTpm               = new bool[PlayerSlot.MaxPlayerCount];

    private const float RAMP_BUG_THRESHOLD = 0.98f;

    private const float RAMP_BUG_VELOCITY_THRESHOLD = 0.95f;
    private const float RAMP_PIERCE_DISTANCE        = 0.15f;
    private const float NEW_RAMP_THRESHOLD          = 0.95f;

    private const           float    FLT_EPSILON      = 1.19209e-07f;
    private static readonly Vector[] OffsetDirections = BuildOffsetDirections();

    private static Vector[] BuildOffsetDirections()
    {
        ReadOnlySpan<float> offsets = [0.0f, -1.0f, 1.0f];

        var dirs = new Vector[27];

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                for (var k = 0; k < 3; k++)
                {
                    dirs[i * 9 + j * 3 + k] = new Vector(offsets[i], offsets[j], offsets[k]).Normalized();
                }
            }
        }

        return dirs;
    }

    private static void OnPreProcessMovement(IPlayerProcessMoveForwardParams obj)
    {
        var slot = obj.Client.Slot;
        DidTpm[slot] = false;
    }

    private static void OnPostProcessMovement(IPlayerProcessMoveForwardParams obj)
    {
        var slot = obj.Client.Slot;

        if (!DidTpm[slot])
        {
            LastValidPlaneNormal[slot] = new ();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsTraceBasicallyValid(CGameTrace* trace)
    {
        if (trace->StartInSolid)
        {
            return false;
        }

        if (trace->Fraction                    < 1.0f
            && MathF.Abs(trace->PlaneNormal.X) < FLT_EPSILON
            && MathF.Abs(trace->PlaneNormal.Y) < FLT_EPSILON
            && MathF.Abs(trace->PlaneNormal.Z) < FLT_EPSILON)
        {
            return false;
        }

        if (MathF.Abs(trace->PlaneNormal.X)    > 1.0f
            || MathF.Abs(trace->PlaneNormal.Y) > 1.0f
            || MathF.Abs(trace->PlaneNormal.Z) > 1.0f)
        {
            return false;
        }

        return true;
    }

    // The expensive half: two extra native traces verifying the end position
    // isn't stuck. Split out so hot loops can defer / skip it.
    [SkipLocalsInit]
    private static unsafe bool VerifyTraceEndNotStuck(CGameTrace* trace, TraceShapeRay* ray, CTraceFilter* filter)
    {
        var stuck = stackalloc CGameTrace[1];

        TracePlayerBBox(&trace->EndPosition, &trace->EndPosition, ray, filter, stuck);

        if (stuck->StartInSolid || stuck->Fraction < 1.0f - FLT_EPSILON)
        {
            return false;
        }

        TracePlayerBBox(&trace->EndPosition, &trace->StartPosition, ray, filter, stuck);

        return !stuck->StartInSolid;
    }

    private static unsafe bool IsValidMovementTrace(CGameTrace* trace, TraceShapeRay* ray, CTraceFilter* filter)
        => IsTraceBasicallyValid(trace) && VerifyTraceEndNotStuck(trace, ray, filter);

    // -1 = not traced yet, 0 = stuck, 1 = clear.
    // Lets the caller put the two traces last in a || chain without ever paying for them twice.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool IsTraceEndVerified(CGameTrace*   trace,
                                                  TraceShapeRay* ray,
                                                  CTraceFilter*  filter,
                                                  ref int        cached)
    {
        if (cached < 0)
        {
            cached = VerifyTraceEndNotStuck(trace, ray, filter) ? 1 : 0;
        }

        return cached != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClipVelocity(in Vector @in, in Vector normal, out Vector @out, float overbounce = 1.0f)
    {
        if (normal == default)
        {
            @out = @in;

            return;
        }

        var backoff = (@in.X * normal.X + @in.Y * normal.Y + @in.Z * normal.Z) * overbounce;

        @out = @in - normal * backoff;

        if (MathF.Abs(@out.X) < 1e-6f) @out.X = 0;
        if (MathF.Abs(@out.Y) < 1e-6f) @out.Y = 0;
        if (MathF.Abs(@out.Z) < 1e-6f) @out.Z = 0;
    }

    [SkipLocalsInit]
    private static unsafe void PreTryPlayerMove(nint        service,
                                                nint        pawn,
                                                PlayerSlot  slot,
                                                MoveData*   mv,
                                                Vector*     pFirstDest,
                                                CGameTrace* pFirstTrace)
    {
        var frameTime = *(float*) (g_pGlobalVars + CGlobalVars_FrametimeOffset);
        var timeLeft  = frameTime;
        var start     = mv->AbsOrigin;
        var end       = new Vector();

        var allFraction = 0.0f;

        var velocity       = mv->Velocity;
        var primalVelocity = velocity;

        var potentiallyStuck = false;

        var pm     = stackalloc CGameTrace[1];
        var pierce = stackalloc CGameTrace[1];

        var ray = new TraceShapeRay(new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, CCSPlayer_MovementServices_IsDucked(service) ? 54.0f : 72.0f),
        });

        var filter    = stackalloc CTraceFilter[1];
        *filter = default;

        var attribute = RnQueryShapeAttr.PlayerMovement(CBaseEntity_GetInteractsWithLayers(pawn));

        attribute.m_nEntityIdsToIgnore[0] = CBaseEntity_GetRefHandle(pawn);
        attribute.m_nHierarchyIds[0]      = CBaseEntity_GetHierarchyId(pawn);

        filter->QueryAttribute = attribute;
        filter->Vtable         = (CTraceFilterVirtualTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;

        var numPlanes = 0;

        var planes = stackalloc Vector[5];

        var test = stackalloc CGameTrace[1];

        ref var lastPlane = ref LastValidPlaneNormal[slot]; // single bounds check for the whole method

        // Neither can change while we simulate - we never re-enter engine movement code here.
        var isWalkingInAir = CBaseEntity_GetMovetype(pawn) == MoveType.Walk && !CBaseEntity_IsOnGround(pawn);

        var overrodeTpm = false;

        for (var bumpCount = 0u; bumpCount < 4; bumpCount++)
        {
            end = start + (velocity * timeLeft);

            Vector pmN; // pm->PlaneNormal, unit-length or exactly zero

            if (pFirstDest != null && *pFirstDest == end)
            {
                *pm = *pFirstTrace;
                pmN = pm->PlaneNormal.Normalized();
            }
            else
            {
                TracePlayerBBox(&start, &end, &ray, filter, pm);

                if (start == end)
                {
                    // Nothing left to sweep. start/velocity/timeLeft can no longer change, so every
                    // remaining bump would re-run this exact degenerate trace and land on the same
                    // pm - break out instead of burning three more traces on it.
                    break;
                }

                var basicValid = IsTraceBasicallyValid(pm);
                var verified   = -1;

                if (basicValid && MathF.Abs(pm->Fraction - 1.0f) < FLT_EPSILON)
                {
                    verified = VerifyTraceEndNotStuck(pm, &ray, filter) ? 1 : 0;

                    if (verified == 1)
                    {
                        break;
                    }
                }

                var lastN = lastPlane; // already unit-length, no sqrt
                pmN = pm->PlaneNormal.Normalized();

                // The two verification traces are the expensive half of the old IsValidMovementTrace,
                // so they go last in the || chain: any cheaper term that short-circuits first (no
                // previous plane, failed the trace-free validity checks, plane changed too much,
                // stuck at fraction 0) now skips them entirely. Same accept/reject outcome.
                if (lastN != default
                    && (!basicValid
                        || pmN.Dot(lastN) < RAMP_BUG_THRESHOLD
                        || potentiallyStuck && pm->Fraction == 0.0f
                        || !IsTraceEndVerified(pm, &ray, filter, ref verified)))
                {
                    var success = false;

                    test[0] = default;

                    for (var d = 0; d < 27 && !success; d++)
                    {
                        Vector offsetDirection;

                        if (d == 0)
                        {
                            offsetDirection = lastN;
                        }
                        else
                        {
                            offsetDirection = OffsetDirections[d]; // precomputed unit vector

                            if (lastN.Dot(offsetDirection) <= 0.0f)
                            {
                                continue;
                            }

                            var testStart = start + offsetDirection * RAMP_PIERCE_DISTANCE;
                            TracePlayerBBox(&testStart, &start, &ray, filter, test);

                            if (!IsValidMovementTrace(test, &ray, filter))
                            {
                                continue;
                            }
                        }

                        var goodTrace   = false;
                        var hitNewPlane = false;

                        for (var ratio = 0.1f; ratio <= 1.0f; ratio += 0.1f)
                        {
                            var pierceOffset = offsetDirection * (ratio * RAMP_PIERCE_DISTANCE);
                            var ratioStart   = start + pierceOffset;
                            var ratioEnd     = end   + pierceOffset;

                            TracePlayerBBox(&ratioStart,
                                            &ratioEnd,
                                            &ray,
                                            filter,
                                            pierce);

                            // Cheap, trace-free checks first...
                            if (!IsTraceBasicallyValid(pierce))
                            {
                                continue;
                            }

                            if (MathF.Abs(pierce->Fraction - 1.0f) < FLT_EPSILON * 4.0f)
                            {
                                if (VerifyTraceEndNotStuck(pierce, &ray, filter))
                                {
                                    goodTrace = true;
                                    break;
                                }

                                continue;
                            }

                            var pierceN = pierce->PlaneNormal.Normalized();

                            var validPlane = pierce->Fraction      < 1.0f
                                             && pierce->Fraction   > 0.1f
                                             && pierceN.Dot(lastN) >= RAMP_BUG_THRESHOLD;

                            var wouldHitNewPlane = pmN.Dot(pierceN)      < NEW_RAMP_THRESHOLD
                                                   && lastN.Dot(pierceN) > NEW_RAMP_THRESHOLD;

                            var wouldBeGood = validPlane;

                            // ...then pay for the two verification traces only when this
                            // iteration's outcome can actually change anything. If it can't
                            // break the loop (wouldBeGood == false) and wouldn't change the
                            // tracked hitNewPlane flag, then whether the verification passes
                            // (flag set to the same value) or fails (flag left untouched) the
                            // result is identical - so the traces are pure waste. This keeps
                            // accept/reject behaviour bit-identical to the original.
                            if (!wouldBeGood && wouldHitNewPlane == hitNewPlane)
                            {
                                continue;
                            }

                            if (!VerifyTraceEndNotStuck(pierce, &ray, filter))
                            {
                                continue;
                            }

                            hitNewPlane = wouldHitNewPlane;
                            goodTrace   = wouldBeGood;

                            if (goodTrace)
                            {
                                break;
                            }
                        }

                        if (goodTrace || hitNewPlane)
                        {
                            TracePlayerBBox(&pierce->EndPosition, &end, &ray, filter, test);

                            if (!IsValidMovementTrace(test, &ray, filter))
                            {
                                continue;
                            }

                            *pm = *pierce;

                            var denomSq = (end - start).LengthSqr();

                            if (denomSq > 1e-12f)
                            {
                                var deltaSq = (pierce->EndPosition - pierce->StartPosition).LengthSqr();

                                // sqrt(a)/sqrt(b) == sqrt(a/b): one sqrt instead of two
                                pm->Fraction = Math.Clamp(MathF.Sqrt(deltaSq / denomSq), 0.0f, 1.0f);
                            }
                            else
                            {
                                pm->Fraction = 0.0f;
                            }

                            pm->EndPosition = test->EndPosition;

                            if (pierce->PlaneNormal.LengthSqr() > 0.0f)
                            {
                                pm->PlaneNormal = pierce->PlaneNormal;
                                lastPlane       = pierce->PlaneNormal.Normalized();
                            }
                            else
                            {
                                pm->PlaneNormal = test->PlaneNormal;
                                lastPlane       = test->PlaneNormal.Normalized();
                            }

                            success     = true;
                            overrodeTpm = true;

                            // *pm's normal was just replaced; lastPlane already holds its unit form.
                            pmN = lastPlane;
                        }
                    }
                }

                if (pmN != default) // Normalized() returns unit-length or exact zero
                {
                    lastPlane = pmN;
                }

                potentiallyStuck = pm->Fraction == 0.0f;
            }

            var fraction = pm->Fraction;

            // original: fraction * |velocity| > 0.03125 - squared to avoid the sqrt
            if (fraction * fraction * velocity.LengthSqr() > 0.03125f * 0.03125f || fraction > 0.03125f)
            {
                allFraction += fraction;
                start       =  pm->EndPosition;
                numPlanes   =  0;
            }

            if (MathF.Abs(allFraction - 1.0f) < FLT_EPSILON)
            {
                break;
            }

            timeLeft -= frameTime * pm->Fraction;

            if (numPlanes >= 5 || (pm->PlaneNormal.Z >= 0.7f && velocity.Length2D() < 1.0f))
            {
                velocity = EmptyVector;

                break;
            }

            planes[numPlanes] = pmN;
            numPlanes++;

            if (numPlanes == 1 && isWalkingInAir)
            {
                ClipVelocity(velocity, planes[0], out velocity);
            }
            else
            {
                int i;

                for (i = 0; i < numPlanes; i++)
                {
                    ClipVelocity(velocity, planes[i], out velocity);

                    int j;

                    for (j = 0; j < numPlanes; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        // Are we now moving against this plane?
                        if (velocity.Dot(planes[j]) < 0)
                        {
                            break; // not ok
                        }
                    }

                    if (j == numPlanes) // Didn't have to clip, so we're ok
                    {
                        break;
                    }
                }

                // Did we go all the way through plane set
                if (i != numPlanes)
                {
                    // go along this plane
                    // pmove.velocity is set in clipping call, no need to set again.
                }
                else
                {
                    // go along the crease
                    if (numPlanes != 2)
                    {
                        velocity = EmptyVector;

                        break;
                    }

                    var dir = planes[0].Cross(planes[1]).Normalized();
                    velocity = dir * dir.Dot(velocity);

                    if (velocity.Dot(primalVelocity) <= 0)
                    {
                        velocity = EmptyVector;

                        break;
                    }
                }
            }
        }

        TpmOrigin[slot]    = pm->EndPosition;
        TpmVelocity[slot]  = velocity;
        OverridenTpm[slot] = overrodeTpm;
    }

    private static readonly Vector EmptyVector = new ();

    private static unsafe void PostTryPlayerMove(MoveData* mv, PlayerSlot slot)
    {
        if (!OverridenTpm[slot])
        {
            return;
        }

        ref var tpmOrigin   = ref TpmOrigin[slot];
        ref var tpmVelocity = ref TpmVelocity[slot];

        if (tpmOrigin == EmptyVector || tpmVelocity == EmptyVector)
        {
            return;
        }

        var tpmLenSq = tpmVelocity.LengthSqr();
        var mvLenSq  = mv->Velocity.LengthSqr();
        var denomSq  = tpmLenSq * mvLenSq;

        var cos = denomSq > 1e-24f
            ? tpmVelocity.Dot(mv->Velocity) / MathF.Sqrt(denomSq)
            : 0.0f;

        var velocityHeavilyModified =
            cos < RAMP_BUG_THRESHOLD
            || tpmLenSq > 50.0f                       * 50.0f
            && mvLenSq  < RAMP_BUG_VELOCITY_THRESHOLD * RAMP_BUG_VELOCITY_THRESHOLD * tpmLenSq;

        if (velocityHeavilyModified)
        {
            mv->AbsOrigin = tpmOrigin;
            mv->Velocity  = tpmVelocity;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void hk_CCSPlayer_MovementService_TryPlayerMove(nint        servicePtr,
                                                                          MoveData*   mv,
                                                                          Vector*     pFirstDest,
                                                                          CGameTrace* pFirstTrace,
                                                                          bool*       pIsSurfing)
    {
        var outer = CPlayerPawnComponent_GetOuter(servicePtr);

        if (outer == nint.Zero || !CBaseEntity_IsAlive(outer))
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

            return;
        }

        var slot = CBasePlayerPawn_GetPlayerSlot(outer);

        if (slot == byte.MaxValue)
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

            return;
        }

        DidTpm[slot]       = true;
        OverridenTpm[slot] = false;

        if (mv->Velocity.LengthSqr() == 0)
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

            return;
        }

        PreTryPlayerMove(servicePtr, outer, slot, mv, pFirstDest, pFirstTrace);
        CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);
        PostTryPlayerMove(mv, slot);
    }

    [UnmanagedCallersOnly]
    [SkipLocalsInit]
    private static unsafe void hk_CCSPlayer_MovementService_CategorizePosition(nint      servicePtr,
                                                                               MoveData* mv,
                                                                               bool      stayOnGround)
    {
        if (stayOnGround || mv->Velocity.Z > -64.0f)
        {
            CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);

            return;
        }

        var outer = CPlayerPawnComponent_GetOuter(servicePtr);

        if (outer == nint.Zero || !CBaseEntity_IsAlive(outer))
        {
            goto original;
        }

        var slot = CBasePlayerPawn_GetPlayerSlot(outer);

        if (slot == byte.MaxValue)
        {
            goto original;
        }

        var lastN = LastValidPlaneNormal[slot]; // invariant: unit-length or zero

        if (lastN == default || lastN.Z > 0.7f)
        {
            goto original;
        }

        var ray = new TraceShapeRay(new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, CCSPlayer_MovementServices_IsDucked(servicePtr) ? 54.0f : 72.0f),
        });

        var filter = stackalloc CTraceFilter[1];
        *filter = default;

        var attribute = RnQueryShapeAttr.PlayerMovement(CBaseEntity_GetInteractsWithLayers(outer));

        attribute.m_nEntityIdsToIgnore[0] = CBaseEntity_GetRefHandle(outer);
        attribute.m_nHierarchyIds[0]      = CBaseEntity_GetHierarchyId(outer);

        filter->QueryAttribute     = attribute;
        filter->Vtable             = (CTraceFilterVirtualTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;
        filter->m_bIterateEntities = true;

        var origin       = mv->AbsOrigin;
        var groundOrigin = origin;
        groundOrigin.Z -= 2.0f;

        var trace = stackalloc CGameTrace[1];

        TracePlayerBBox(&origin, &groundOrigin, &ray, filter, trace);

        if (MathF.Abs(trace->Fraction - 1.0f) < FLT_EPSILON)
        {
            goto original;
        }

        if (trace->Fraction                               < 0.95f
            && trace->PlaneNormal.Z                       > 0.7f
            && lastN.Dot(trace->PlaneNormal.Normalized()) < RAMP_BUG_THRESHOLD)
        {
            origin         += lastN * 0.0625f;
            groundOrigin   =  origin;
            groundOrigin.Z -= 2.0f;

            TracePlayerBBox(&origin, &groundOrigin, &ray, filter, trace);

            if (trace->StartInSolid)
            {
                goto original;
            }

            if (MathF.Abs(trace->Fraction - 1.0f)             < FLT_EPSILON
                || lastN.Dot(trace->PlaneNormal.Normalized()) >= RAMP_BUG_THRESHOLD)
            {
                mv->AbsOrigin = origin;
            }
        }

    original:
        CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 1;
}
