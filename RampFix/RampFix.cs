using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
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
    private readonly IDetourHook      _tracePlayerBBoxHook;
    private readonly ILogger<RampFix> _logger;

    private static unsafe delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>
        CCSPlayer_MovementService_TryPlayerMoveOriginal;
    private static unsafe delegate* unmanaged<nint, MoveData*, bool, void> CCSPlayer_MovementService_CategorizePositionOrigin;

    private static unsafe delegate* unmanaged<nint, CGameTrace*, Vector*, Vector*, TraceShapeHull*, CTraceFilter*, void>
        TracePlayerBBoxOriginal;

    private static unsafe delegate* unmanaged<IntPtr, TraceShapeRay*, Vector*, Vector*,
        CTraceFilter*, CGameTrace*, bool> TraceShape;

    private static nint g_pPhysicsQuery = 0;

    private readonly IGameData      _gameData;
    private readonly IHookManager   _hookManager;
    private readonly IConVarManager _conVarManager;

    private static IModSharp _modSharp;

    private IConVar? _standableNormal;

    private static nint CTraceFilterPlayerMovementCS_vtable;

    private static int CPlayerPawnComponent_ChainEntityOffset;
    private static int CBaseEntity_m_lifestateOffset;
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

        _hookManager   = sharedSystem.GetHookManager();
        _conVarManager = sharedSystem.GetConVarManager();

        _modSharp = sharedSystem.GetModSharp();
        _gameData = _modSharp.GetGameData();

        CTraceFilterPlayerMovementCS_vtable = sharedSystem.GetLibraryModuleManager()
                                                          .Server
                                                          .GetVirtualTableByName("CTraceFilterPlayerMovementCS");

        _tryPlayerMoveHook      = _hookManager.CreateDetourHook();
        _categorizePositionHook = _hookManager.CreateDetourHook();
        _tracePlayerBBoxHook    = _hookManager.CreateDetourHook();

        var schemaManager = sharedSystem.GetSchemaManager();

        CPlayerPawnComponent_ChainEntityOffset = schemaManager.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBaseEntity_m_lifestateOffset          = schemaManager.GetNetVarOffset("CBaseEntity", "m_lifeState");
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
    private static unsafe bool CBaseEntity_IsOnGround(nint entity)
        => *(uint*) (entity + CBaseEntity_m_hGroundEntityOffset) != uint.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe nint CBaseEntity_GetCollisionAttribute(nint entity)
        => *(nint*) (entity + CBaseEntity_m_pCollisionOffset) + CCollisionProperty_m_collisionAttributeOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint CBaseEntity_GetRefHandle(nint entity)
    {
        var identity = *(nint*) (entity + 0x10);

        if (identity == nint.Zero)
            return uint.MaxValue;

        var handle = *(uint*) (identity + 0x10);
        var flags  = *(uint*) (identity + 0x30);

        var lo     = handle != 0xFFFFFFFF ? handle & 0x7FFF : 0x7FFF;
        var hi     = (handle >> 15) - (flags & 0x1) << 15;
        var result = lo | hi;

        return result;
    }

    public unsafe bool Init()
    {
        _gameData.Register("rampfix.games");

        if (!_gameData.GetAddress("CGamePhysicsQueryInterface::TraceShape", out var address))
        {
            _logger.LogInformation("Failed to get address for CGamePhysicsQueryInterface::TraceShape");

            return false;
        }

        TraceShape = (delegate* unmanaged<IntPtr, TraceShapeRay*, Vector*, Vector*, CTraceFilter*,
            CGameTrace*, bool>) address;

        if (!_gameData.GetAddress("g_pPhysicsQuery", out address))
        {
            _logger.LogInformation("Failed to get address for g_pPhysicsQuery");

            return false;
        }

        g_pPhysicsQuery = address;

        _tracePlayerBBoxHook.Prepare("TracePlayerBBox",
                                     (nint) (delegate* unmanaged<nint, CGameTrace*, Vector*, Vector*, TraceShapeHull*,
                                         CTraceFilter*, void>) (&hk_TracePlayerBBox));

        _tryPlayerMoveHook.Prepare("CCSPlayer_MovementService::TryPlayerMove",
                                   (nint) (delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>)
                                   (&hk_CCSPlayer_MovementService_TryPlayerMove));

        _categorizePositionHook.Prepare("CCSPlayer_MovementService::CategorizePosition",
                                        (nint) (delegate* unmanaged<nint, MoveData*, bool, void>)
                                        (&hk_CCSPlayer_MovementService_CategorizePosition));

        if (!_tracePlayerBBoxHook.Install())
        {
            _logger.LogInformation("Failed to hook TracePlayerBBox");

            return false;
        }

        TracePlayerBBoxOriginal
            = (delegate* unmanaged<nint, CGameTrace*, Vector*, Vector*, TraceShapeHull*, CTraceFilter*, void>)
            _tracePlayerBBoxHook.Trampoline;

        if (!_tryPlayerMoveHook.Install())
        {
            _logger.LogInformation("Failed to hook CCSPlayer_MovementService::TryPlayerMove");

            return false;
        }

        CCSPlayer_MovementService_TryPlayerMoveOriginal
            = (delegate* unmanaged<nint, MoveData*, Vector*, CGameTrace*, bool*, void>) _tryPlayerMoveHook.Trampoline;

        if (!_categorizePositionHook.Install())
        {
            _logger.LogInformation("Failed to hook CCSPlayer_MovementService::CategorizePosition");

            return false;
        }

        CCSPlayer_MovementService_CategorizePositionOrigin
            = (delegate* unmanaged<nint, MoveData*, bool, void>) _categorizePositionHook.Trampoline;

        _standableNormal = _conVarManager.FindConVar("sv_standable_normal");

        if (_standableNormal is not null)
        {
            StandableNormal = _standableNormal.GetFloat();
            _conVarManager.InstallChangeHook(_standableNormal, OnStandableNormalChanged);
        }
        else
        {
            _logger.LogWarning("Failed to find sv_standable_normal, using {value}", StandableNormal);
        }

        _modSharp.InstallGameListener(this);

        return true;
    }

    private static void OnStandableNormalChanged(IConVar conVar)
    {
        StandableNormal = conVar.GetFloat();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void TraceHull(Vector*        start,
                                         Vector*        end,
                                         TraceShapeRay* ray,
                                         CTraceFilter*  filter,
                                         CGameTrace*    trace)
    {
        TraceShape(g_pPhysicsQuery, ray, start, end, filter, trace);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TraceShapeRay CreatePlayerHullRay(nint service)
        => new (new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, CCSPlayer_MovementServices_IsDucked(service) ? 54.0f : 72.0f),
        });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void InitPlayerMovementFilter(CTraceFilter* filter, nint pawn, bool iterateEntities)
    {
        var collisionAttribute = CBaseEntity_GetCollisionAttribute(pawn);

        *filter = default;

        filter->QueryAttribute = RnQueryShapeAttr.PlayerMovement(
            *(InteractionLayers*) (collisionAttribute + VPhysicsCollisionAttribute_t_m_nInteractsWithOffset));

        filter->QueryAttribute.m_nEntityIdsToIgnore[0] = CBaseEntity_GetRefHandle(pawn);
        filter->QueryAttribute.m_nHierarchyIds[0]
            = *(ushort*) (collisionAttribute + VPhysicsCollisionAttribute_t_m_nHierarchyIdOffset);

        filter->Vtable             = (CTraceFilterVirtualTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;
        filter->m_bIterateEntities = iterateEntities;
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
        if (_standableNormal is not null)
        {
            _conVarManager.RemoveChangeHook(_standableNormal, OnStandableNormalChanged);
        }

        _tryPlayerMoveHook.Uninstall();
        _categorizePositionHook.Uninstall();
        _tracePlayerBBoxHook.Uninstall();

        _tryPlayerMoveHook.Dispose();
        _categorizePositionHook.Dispose();
        _tracePlayerBBoxHook.Dispose();

        _modSharp.RemoveGameListener(this);
    }

    // The game keeps the player this far away from what they collide with.
    private const float RAMP_BUG_OFFSET          = 0.03125f;
    private const float RAMP_BUG_THRESHOLD       = 0.999f;
    private const float RAMP_BUG_SAME_PLANE      = 0.99f;
    private const float RAMP_BUG_MIN_DISTANCE    = 1.0f;
    private const float GROUND_CHECK_SHRINK_SIZE = 0.0625f;

    // Last plane that TryPlayerMove hit.
    private static readonly Vector[] LastPlane = new Vector[PlayerSlot.MaxPlayerCount];

    private static float StandableNormal = 0.7f;

    // The player that is currently running TryPlayerMove or CategorizePosition, -1 if none.
    private static int  _tracingSlot = -1;
    private static bool _tracingInTryPlayerMove;

    // Every hull trace done by TryPlayerMove and CategorizePosition goes through this function.
    [UnmanagedCallersOnly]
    private static unsafe void hk_TracePlayerBBox(nint            traceCache,
                                                  CGameTrace*     pm,
                                                  Vector*         start,
                                                  Vector*         end,
                                                  TraceShapeHull* bounds,
                                                  CTraceFilter*   filter)
    {
        TracePlayerBBoxOriginal(traceCache, pm, start, end, bounds, filter);

        var slot = _tracingSlot;

        if (slot < 0)
        {
            return;
        }

        var ray = new TraceShapeRay(*bounds);

        if (_tracingInTryPlayerMove)
        {
            FixTryPlayerMoveTrace(&ray, *start, *end, filter, pm, ref LastPlane[slot]);
        }
        else
        {
            FixGroundTrace(&ray, *start, *end, filter, pm);
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

        var previousSlot            = _tracingSlot;
        var previousInTryPlayerMove = _tracingInTryPlayerMove;

        _tracingSlot            = slot;
        _tracingInTryPlayerMove = true;

        // WalkMove does the first trace before calling TryPlayerMove, so we need to fix that one here.
        if (pFirstDest != null && pFirstTrace != null && g_pGlobalVars != 0 && CBaseEntity_IsOnGround(outer))
        {
            FixFirstTrace(servicePtr, outer, slot, mv, pFirstDest, pFirstTrace);
        }

        CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

        _tracingSlot            = previousSlot;
        _tracingInTryPlayerMove = previousInTryPlayerMove;
    }

    [SkipLocalsInit]
    private static unsafe void FixFirstTrace(nint        service,
                                             nint        pawn,
                                             byte        slot,
                                             MoveData*   mv,
                                             Vector*     pFirstDest,
                                             CGameTrace* pFirstTrace)
    {
        var frametime = *(float*) (g_pGlobalVars + CGlobalVars_FrametimeOffset);
        var origin    = mv->AbsOrigin;
        var end       = origin + (mv->Velocity * frametime);

        // TryPlayerMove only uses it if it ends exactly where its own first trace would end.
        // Vector's == has a tolerance, so compare the components directly.
        if (end.X != pFirstDest->X || end.Y != pFirstDest->Y || end.Z != pFirstDest->Z)
        {
            return;
        }

        var ray = CreatePlayerHullRay(service);

        var filter = stackalloc CTraceFilter[1];
        InitPlayerMovementFilter(filter, pawn, iterateEntities: true);

        FixTryPlayerMoveTrace(&ray, origin, end, filter, pFirstTrace, ref LastPlane[slot]);
    }

    [UnmanagedCallersOnly]
    private static unsafe void hk_CCSPlayer_MovementService_CategorizePosition(nint      servicePtr,
                                                                               MoveData* mv,
                                                                               bool      stayOnGround)
    {
        var outer = CPlayerPawnComponent_GetOuter(servicePtr);

        if (outer == nint.Zero || !CBaseEntity_IsAlive(outer))
        {
            CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);

            return;
        }

        var slot = CBasePlayerPawn_GetPlayerSlot(outer);

        if (slot == byte.MaxValue)
        {
            CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);

            return;
        }

        var previousSlot            = _tracingSlot;
        var previousInTryPlayerMove = _tracingInTryPlayerMove;

        _tracingSlot            = slot;
        _tracingInTryPlayerMove = false;

        CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);

        _tracingSlot            = previousSlot;
        _tracingInTryPlayerMove = previousInTryPlayerMove;
    }

    /*
        Rampbug fix:
        The game keeps the player 1/32 unit away from whatever they collide with.
        While sliding on a ramp, the player can end up closer to the ramp than that, or collide with the edge between two triangles of the ramp
        instead of the ramp itself, which gives a wrong normal. TryPlayerMove then either stops the player or sends them in a random direction.

        To fix this, we redo the trace a bit further away from the ramp. We only use that trace if it actually goes further than the original one.
        If it just ends up on another plane right away, the player is really running into a corner and the original trace is kept.
    */
    [SkipLocalsInit]
    private static unsafe void FixTryPlayerMoveTrace(TraceShapeRay* ray,
                                                     Vector         start,
                                                     Vector         end,
                                                     CTraceFilter*  filter,
                                                     CGameTrace*    pm,
                                                     ref Vector     lastPlane)
    {
        var direction = end - start;
        var length    = direction.Length();

        if (length > 0.0f)
        {
            direction *= 1.0f / length;
        }

        // We hit a plane that will change our velocity, or we barely moved at all. This is either a real obstacle or a rampbug.
        if (pm->Fraction < 1.0f
            && !pm->StartInSolid
            && lastPlane.LengthSqr() > 0.0f
            && length                > 0.0f
            && (pm->PlaneNormal.Dot(lastPlane) < RAMP_BUG_THRESHOLD || pm->Fraction * length < RAMP_BUG_OFFSET))
        {
            var trace = stackalloc CGameTrace[1];

            // This can only be a rampbug if the player is still right next to the last plane they hit.
            var testStart = pm->EndPosition + (lastPlane * RAMP_BUG_OFFSET);
            var testEnd   = pm->EndPosition - (lastPlane * (RAMP_BUG_OFFSET * 2.0f));
            TraceHull(&testStart, &testEnd, ray, filter, trace);

            if (trace->Fraction < 1.0f && !trace->StartInSolid && trace->PlaneNormal.Dot(lastPlane) >= RAMP_BUG_THRESHOLD)
            {
                // Redo the trace a bit further away from the plane.
                var offset      = lastPlane * RAMP_BUG_OFFSET;
                var offsetStart = start + offset;
                var offsetEnd   = end   + offset;
                TraceHull(&offsetStart, &offsetEnd, ray, filter, trace);

                // If this trace only goes a bit further and then hits a different plane, the original trace hit something real,
                // like a corner or two ramps meeting each other. Keep the original so TryPlayerMove can slide along both planes.
                var distance  = (trace->EndPosition - pm->EndPosition).Dot(direction);
                var samePlane = trace->PlaneNormal.Dot(lastPlane) >= RAMP_BUG_SAME_PLANE;

                if (!trace->StartInSolid
                    && distance > RAMP_BUG_OFFSET
                    && (trace->Fraction == 1.0f || distance > RAMP_BUG_MIN_DISTANCE || samePlane))
                {
                    *pm = *trace;
                }
            }
        }

        FixGroundTrace(ray, start, end, filter, pm);

        if (pm->Fraction < 1.0f && !pm->StartInSolid && pm->PlaneNormal.LengthSqr() > 0.98f)
        {
            lastPlane = pm->PlaneNormal;
        }
    }

    // Sometimes the game thinks the player is on standable ground while they are actually on a steep slope right below a wall.
    // A slightly smaller hull doesn't have this problem and will hit the slope instead.
    // It will also hit a slope if the player is standing over a gap between two floors though, so we make sure there's a wall next to the player.
    [SkipLocalsInit]
    private static unsafe void FixGroundTrace(TraceShapeRay* ray,
                                              Vector         start,
                                              Vector         end,
                                              CTraceFilter*  filter,
                                              CGameTrace*    pm)
    {
        var standableZ = StandableNormal;

        if (pm->Fraction == 1.0f || pm->StartInSolid || pm->PlaneNormal.Z < standableZ)
        {
            return;
        }

        var shrink   = new Vector(GROUND_CHECK_SHRINK_SIZE, GROUND_CHECK_SHRINK_SIZE, 0.0f);
        var smallRay = new TraceShapeRay(new TraceShapeHull { Mins = ray->Hull.Mins + shrink, Maxs = ray->Hull.Maxs - shrink });

        var trace = stackalloc CGameTrace[1];
        TraceHull(&start, &end, &smallRay, filter, trace);

        if (trace->Fraction == 1.0f || trace->StartInSolid || trace->PlaneNormal.Z >= standableZ)
        {
            return;
        }

        var slopeNormal = trace->PlaneNormal;

        // Is there a wall on the side that the slope goes up towards?
        var wallDirection = new Vector(-slopeNormal.X, -slopeNormal.Y, 0.0f);
        wallDirection.Normalize();

        if (wallDirection.LengthSqr() == 0.0f)
        {
            return;
        }

        var wallStart = pm->EndPosition;
        var wallEnd   = wallStart + (wallDirection * GROUND_CHECK_SHRINK_SIZE);
        TraceHull(&wallStart, &wallEnd, ray, filter, trace);

        if (trace->Fraction < 0.5f && !trace->StartInSolid)
        {
            pm->PlaneNormal = slopeNormal;
        }
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 1;
}
