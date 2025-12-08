using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

[assembly: DisableRuntimeMarshalling]

namespace RampFix;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct bbox_t
{
    public Vector mins;
    public Vector maxs;
}

public class RampFix : IModSharpModule
{
    string IModSharpModule.DisplayName   => "RampFix";
    string IModSharpModule.DisplayAuthor => "zer0.k, ported by Nukoooo";

    private readonly IDetourHook      _tryPlayerMoveHook;
    private readonly IDetourHook      _categorizePositionHook;
    private readonly ILogger<RampFix> _logger;

    private static unsafe delegate* unmanaged<nint, MoveData*, Vector*, GameTrace*, bool*, void>
        CCSPlayer_MovementService_TryPlayerMoveOriginal;
    private static unsafe delegate* unmanaged<nint, MoveData*, bool, void> CCSPlayer_MovementService_CategorizePositionOrigin;

    private static unsafe delegate* unmanaged<Vector*, Vector*, bbox_t*, CTraceFilter*, GameTrace*, void> TracePlayerBBox;

    private readonly IGameData    _gameData;
    private readonly IHookManager _hookManager;

    private static IModSharp   _modSharp;
    private static IGlobalVars _globalVars => _modSharp.GetGlobals();

    private static nint CTraceFilterPlayerMovementCS_vtable;

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
    }

    public unsafe bool Init()
    {
        _gameData.Register("rampfix.games");

        if (!_modSharp.GetGameData().GetAddress("CCSPlayer_MovementServices::TracePlayerBBox", out var address))
        {
            _logger.LogInformation("Failed to get address for CCSPlayer_MovementServices::TracePlayerBBox");

            return false;
        }

        TracePlayerBBox = (delegate* unmanaged<Vector*, Vector*, bbox_t*, CTraceFilter*, GameTrace*, void>) address;

        _tryPlayerMoveHook.Prepare("CCSPlayer_MovementService::TryPlayerMove",
                                   (nint) (delegate* unmanaged<nint, MoveData*, Vector*, GameTrace*, bool*, void>)
                                   (&hk_CCSPlayer_MovementService_TryPlayerMove));

        _categorizePositionHook.Prepare("CCSPlayer_MovementService::CategorizePosition",
                                        (nint) (delegate* unmanaged<nint, MoveData*, bool, void>)
                                        (&hk_CCSPlayer_MovementService_CategorizePosition));

        if (_tryPlayerMoveHook.Install())
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal
                = (delegate* unmanaged<nint, MoveData*, Vector*, GameTrace*, bool*, void>) _tryPlayerMoveHook.Trampoline;
        }

        if (_categorizePositionHook.Install())
        {
            CCSPlayer_MovementService_CategorizePositionOrigin
                = (delegate* unmanaged<nint, MoveData*, bool, void>) _categorizePositionHook.Trampoline;

            return true;
        }

        _hookManager.PlayerProcessMovePost.InstallForward(OnPostProcessMovement);
        _hookManager.PlayerProcessMovePre.InstallForward(OnPreProcessMovement);

        return false;
    }

    public void Shutdown()
    {
        _hookManager.PlayerProcessMovePost.RemoveForward(OnPostProcessMovement);
        _hookManager.PlayerProcessMovePre.RemoveForward(OnPreProcessMovement);

        _tryPlayerMoveHook.Uninstall();
        _categorizePositionHook.Uninstall();

        _tryPlayerMoveHook.Dispose();
        _categorizePositionHook.Dispose();
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

    private static unsafe bool IsValidMovementTrace(GameTrace* trace, bbox_t* bbox, CTraceFilter* filter)
    {
        if (trace->StartInSolid)
        {
            return false;
        }

        if (trace->Fraction                    < 1.0f
            && MathF.Abs(trace->PlaneNormal.X) < float.Epsilon
            && MathF.Abs(trace->PlaneNormal.Y) < float.Epsilon
            && MathF.Abs(trace->PlaneNormal.Z) < float.Epsilon)
        {
            return false;
        }

        if (MathF.Abs(trace->PlaneNormal.X)    > 1.0f
            || MathF.Abs(trace->PlaneNormal.Y) > 1.0f
            || MathF.Abs(trace->PlaneNormal.Z) > 1.0f)
        {
            return false;
        }

        var stuck = stackalloc GameTrace[1];
        TracePlayerBBox(&trace->EndPosition, &trace->EndPosition, bbox, filter, stuck);

        if (stuck->StartInSolid || stuck->Fraction < 1.0f - float.Epsilon)
        {
            return false;
        }

        TracePlayerBBox(&trace->EndPosition, &trace->StartPosition, bbox, filter, stuck);

        if (stuck->StartInSolid)
        {
            return false;
        }

        return true;
    }

    private static readonly Vector[] LastValidPlaneNormal = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] TpmOrigin            = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] TpmVelocity          = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   OverridenTpm         = new bool[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   DidTpm               = new bool[PlayerSlot.MaxPlayerCount];

    private const float RAMP_BUG_THRESHOLD          = 0.98f;

    private const float RAMP_BUG_VELOCITY_THRESHOLD = 0.95f;
    private const float RAMP_PIERCE_DISTANCE        = 0.0625f;
    private const float NEW_RAMP_THRESHOLD          = 0.95f;

    private static void ClipVelocity(in Vector @in, in Vector normal, out Vector @out)
    {
        var backoff = -((@in.X * normal.X) + ((normal.Z * @in.Z) + (@in.Y * normal.Y))) * 1;
        backoff = MathF.Max(backoff, 0.0f) + 0.03125f;

        @out = (normal * backoff) + @in;
    }

    private static unsafe void PreTryPlayerMove(IMovementService service,
                                                IBasePlayerPawn  pawn,
                                                PlayerSlot       slot,
                                                MoveData*        mv,
                                                Vector*          pFirstDest,
                                                GameTrace*       pFirstTrace)
    {
        var timeLeft = _globalVars.FrameTime;
        var start    = mv->AbsOrigin;
        var end      = new Vector();

        var allFraction = 0.0f;

        var velocity       = mv->Velocity;
        var primalVelocity = velocity;

        var potentiallyStuck = false;

        var pm     = stackalloc GameTrace[1];
        var pierce = stackalloc GameTrace[1];

        var bbox = new bbox_t
        {
            mins = new (-16, -16, 0),
            maxs = new (-16, -16, service.GetNetVar<bool>("m_bDucked") ? 54.0f : 72.0f),
        };

        var filter    = stackalloc CTraceFilter[1];
        var collision = pawn.GetCollisionProperty()!;
        filter->QueryAttribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
        filter->Vtable         = (CTraceFilterVTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;

        var numPlanes = 0;

        var planes = stackalloc Vector[5];

        for (var bumpCount = 0; bumpCount < 8; bumpCount++)
        {
            end = start + (velocity * timeLeft);

            if (pFirstDest != null && *pFirstDest == end)
            {
                *pm = *pFirstTrace;
            }
            else
            {
                TracePlayerBBox(&start, &end, &bbox, filter, pm);

                if (start == end)
                {
                    continue;
                }

                var isValidTrace = IsValidMovementTrace(pm, &bbox, filter);

                if (isValidTrace && Math.Abs(pm->Fraction - 1.0f) < float.Epsilon)
                {
                    break;
                }

                if (LastValidPlaneNormal[slot].Length() > float.Epsilon
                    && (!isValidTrace
                        || (pm->PlaneNormal.Dot(LastValidPlaneNormal[slot]) < RAMP_BUG_THRESHOLD)
                        | (potentiallyStuck && pm->Fraction == 0.0f)))
                {
                    float[] offsets = [0.0f, -1.0f, 1.0f];
                    var     success = false;

                    for (var i = 0; i < 3 && !success; i++)
                    {
                        for (var j = 0; j < 3 && !success; j++)
                        {
                            for (var k = 0; k < 3 && !success; k++)
                            {
                                Vector offsetDirection;

                                if (i == 0 && j == 0 && k == 0)
                                {
                                    offsetDirection = LastValidPlaneNormal[slot];
                                }
                                else
                                {
                                    offsetDirection = new (offsets[i], offsets[j], offsets[k]);

                                    if (LastValidPlaneNormal[slot].Dot(offsetDirection) <= 0.0f)
                                    {
                                        continue;
                                    }

                                    var test      = new GameTrace();
                                    var testStart = start + (offsetDirection * RAMP_PIERCE_DISTANCE);
                                    TracePlayerBBox(&testStart, &start, &bbox, filter, &test);

                                    if (!IsValidMovementTrace(&test, &bbox, filter))
                                    {
                                        continue;
                                    }
                                }

                                var goodTrace   = false;
                                var hitNewPlane = false;

                                for (var ratio = 0.25f; ratio <= 1.0f; ratio += 0.25f)
                                {
                                    var ratioStart = start + (offsetDirection * ratio * RAMP_PIERCE_DISTANCE);
                                    var ratioEnd   = end   + (offsetDirection * ratio * RAMP_PIERCE_DISTANCE);

                                    TracePlayerBBox(&ratioStart,
                                                    &ratioEnd,
                                                    &bbox,
                                                    filter,
                                                    pierce);

                                    if (!IsValidMovementTrace(pierce, &bbox, filter))
                                    {
                                        continue;
                                    }

                                    var validPlane = pierce->Fraction    < 1.0f
                                                     && pierce->Fraction > 0.1f
                                                     && pierce->PlaneNormal.Dot(LastValidPlaneNormal[slot])
                                                     >= RAMP_BUG_THRESHOLD;

                                    hitNewPlane = pm->PlaneNormal.Dot(pierce->PlaneNormal)               < NEW_RAMP_THRESHOLD
                                                  && LastValidPlaneNormal[slot].Dot(pierce->PlaneNormal) > NEW_RAMP_THRESHOLD;

                                    goodTrace = MathF.Abs(pierce->Fraction - 1.0f) < float.Epsilon || validPlane;

                                    if (goodTrace)
                                    {
                                        break;
                                    }
                                }

                                if (goodTrace || hitNewPlane)
                                {
                                    var test = new GameTrace();
                                    TracePlayerBBox(&pierce->EndPosition, &end, &bbox, filter, &test);

                                    *pm = *pierce;

                                    Unsafe.AsRef(in pm->StartPosition) = start;

                                    Unsafe.AsRef(in pm->Fraction)
                                        = Math.Clamp((pierce->EndPosition - pierce->StartPosition).Length()
                                                     / (end               - start).Length(),
                                                     0.0f,
                                                     1.0f);

                                    Unsafe.AsRef(in pm->EndPosition) = test.EndPosition;

                                    if (pierce->PlaneNormal.LengthSqr() > 0.0f)
                                    {
                                        Unsafe.AsRef(in pm->PlaneNormal) = pierce->PlaneNormal;
                                        LastValidPlaneNormal[slot]       = pierce->PlaneNormal;
                                    }
                                    else
                                    {
                                        Unsafe.AsRef(in pm->PlaneNormal) = test.PlaneNormal;
                                        LastValidPlaneNormal[slot]       = test.PlaneNormal;
                                    }

                                    success            = true;
                                    OverridenTpm[slot] = true;
                                }
                            }
                        }
                    }
                }

                if (pm->PlaneNormal.Length() > 0.99f)
                {
                    LastValidPlaneNormal[slot] = pm->PlaneNormal;
                }

                potentiallyStuck = pm->Fraction == 0.0f;
            }

            if (pm->Fraction * velocity.Length() > 0.03125f || pm->Fraction > 0.03125f)
            {
                allFraction += pm->Fraction;
                start       =  pm->EndPosition;
                numPlanes   =  0;
            }

            if (Math.Abs(allFraction - 1.0f) < float.Epsilon)
            {
                break;
            }

            timeLeft -= _globalVars.FrameTime * pm->Fraction;

            if (numPlanes >= 5 || (pm->PlaneNormal.Z >= 0.7f && velocity.Length2D() < 1.0f))
            {
                velocity = new ();

                break;
            }

            planes[numPlanes] = pm->PlaneNormal;
            numPlanes++;

            if (numPlanes == 1 && pawn.MoveType == MoveType.Walk && !pawn.GroundEntityHandle.IsValid())
            {
                ClipVelocity(velocity, planes[0], out velocity);
            }
            else
            {
                uint i;

                for (i = 0; i < numPlanes; i++)
                {
                    ClipVelocity(velocity, planes[i], out velocity);
                    uint j;

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
                        velocity = new ();

                        break;
                    }

                    var   dir = planes[0].Cross(planes[1]);
                    float d;
                    dir.Normalize();
                    dir.Normalize();
                    d = dir.Dot(velocity);

                    if (velocity.Dot(primalVelocity) < d)
                    {
                        velocity = new ();

                        break;
                    }
                }
            }
        }

        TpmOrigin[slot]   = pm->EndPosition;
        TpmVelocity[slot] = velocity;
    }

    private static unsafe void PostTryPlayerMove(MoveData* mv, PlayerSlot slot)
    {
        var normalizedTpmVelocity = TpmVelocity[slot];
        normalizedTpmVelocity.Normalize();

        var normalizedVelocity = mv->Velocity;
        normalizedVelocity.Normalize();

        var heavilyModified = normalizedTpmVelocity.Dot(normalizedVelocity) < RAMP_BUG_THRESHOLD
                              || (TpmVelocity[slot].Length()
                                  > 50.0f
                                  && mv->Velocity.Length() / TpmVelocity[slot].Length() < RAMP_BUG_VELOCITY_THRESHOLD);

        if (OverridenTpm[slot] && heavilyModified && !TpmOrigin[slot].IsZero() && !TpmVelocity[slot].IsZero())
        {
            mv->AbsOrigin = TpmOrigin[slot];
            mv->Velocity  = TpmVelocity[slot];
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void hk_CCSPlayer_MovementService_TryPlayerMove(nint       servicePtr,
                                                                          MoveData*  mv,
                                                                          Vector*    pFirstDest,
                                                                          GameTrace* pFirstTrace,
                                                                          bool*      pIsSurfing)
    {
        var service = _modSharp.CreateNativeObject<IPlayerMovementService>(servicePtr)!;

        if (service.GetPlayer() is not { } pawn
            || pawn.GetController() is not { } controller)
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

            return;
        }

        var slot = controller.PlayerSlot;
        DidTpm[slot] = true;

        if (mv->Velocity.LengthSqr() == 0)
        {
            CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);

            return;
        }

        PreTryPlayerMove(service, pawn, slot, mv, pFirstDest, pFirstTrace);
        CCSPlayer_MovementService_TryPlayerMoveOriginal(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);
        PostTryPlayerMove(mv, slot);
    }

    [UnmanagedCallersOnly]
    private static unsafe void hk_CCSPlayer_MovementService_CategorizePosition(nint      servicePtr,
                                                                               MoveData* mv,
                                                                               bool      stayOnGround)
    {
        var service = _modSharp.CreateNativeObject<IPlayerMovementService>(servicePtr)!;

        if (service.GetPlayer() is not { } basePlayerPawn
            || basePlayerPawn.AsPlayer() is not { IsAlive: true } pawn
            || pawn.GetController() is not { } controller)
        {
            goto original;
        }

        var slot = controller.PlayerSlot;

        if (stayOnGround || LastValidPlaneNormal[slot].Length() < float.Epsilon || LastValidPlaneNormal[slot].Z > 0.7f)
        {
            goto original;
        }

        if (mv->Velocity.Z > -64.0f)
        {
            goto original;
        }

        var bbox = new bbox_t
        {
            mins = new (-16, -16, 0),
            maxs = new (-16, -16, service.GetNetVar<bool>("m_bDucked") ? 54.0f : 72.0f),
        };

        var filter    = stackalloc CTraceFilter[1];
        var collision = pawn.GetCollisionProperty()!;
        filter->QueryAttribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
        filter->Vtable         = (CTraceFilterVTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;

        var origin       = mv->AbsOrigin;
        var groundOrigin = origin;
        groundOrigin.Z -= 2.0f;

        var trace = stackalloc GameTrace[1];

        TracePlayerBBox(&origin, &groundOrigin, &bbox, filter, trace);

        if (Math.Abs(trace->Fraction - 1.0f) < float.Epsilon)
        {
            goto original;
        }

        if (trace->Fraction                                       < 0.95f
            && trace->PlaneNormal.Z                               > 0.7f
            && LastValidPlaneNormal[slot].Dot(trace->PlaneNormal) < RAMP_BUG_THRESHOLD)
        {
            origin         += LastValidPlaneNormal[slot] * 0.0625f;
            groundOrigin   =  origin;
            groundOrigin.Z -= 2.0f;

            TracePlayerBBox(&origin, &groundOrigin, &bbox, filter, trace);

            if (trace->StartInSolid)
            {
                goto original;
            }

            if (Math.Abs(trace->Fraction - 1.0f)                      < float.Epsilon
                || LastValidPlaneNormal[slot].Dot(trace->PlaneNormal) >= RAMP_BUG_THRESHOLD)
            {
                mv->AbsOrigin = origin;
            }
        }

    original:
        CCSPlayer_MovementService_CategorizePositionOrigin(servicePtr, mv, stayOnGround);
    }
}
