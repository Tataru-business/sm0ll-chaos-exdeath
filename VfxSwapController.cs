using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using InteropGenerator.Runtime;

namespace DMUModelScale;

/// <summary>Redirects only the selected fight visuals; action effects and hit detection stay untouched.</summary>
internal sealed unsafe class VfxSwapController : IDisposable
{
    private const string ActorVfxCreateSignature =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";

    private const string P1Fire = "vfx/monster/gimmick3/eff/n4g1_b0_g06c0c.avfx";
    private const string GarudaSlipstream = "vfx/monster/m0095/eff/m0095sp_04tf.avfx";
    private const string HiddenVfx = "vfx/path/nothing.avfx";
    private const string DancingDeepCut0 = "vfx/monster/m0934/eff/m0934sp_04c0x.avfx";
    private const string DancingDeepCut1 = "vfx/monster/m0934/eff/m0934sp_04c1x.avfx";
    private const string DancingDropTheNeedle = "vfx/monster/m0934/eff/m0934sp_09c0x.avfx";
    private const string DancingForsaken0 = "vfx/monster/m0934/eff/m0934sp_30c0x.avfx";
    private const string DancingForsaken1 = "vfx/monster/m0934/eff/m0934sp_28c1x.avfx";
    private const string DancingCleaveLeft0 = "vfx/monster/m0934/eff/m0934sp_05c0x.avfx";
    private const string DancingCleaveLeft1 = "vfx/monster/m0934/eff/m0934sp_05c1x.avfx";
    private const string DancingCleaveRight = "vfx/monster/m0934/eff/m0934sp_06c0x.avfx";

    private delegate VfxObject* ActorVfxCreateDelegate(
        string path, nint caster, nint target, float duration, char unknown1, ushort unknown2, char unknown3);

    private readonly Hook<ActorVfxCreateDelegate>? actorHook;
    private readonly Hook<VfxObject.Delegates.Create>? staticHook;
    private volatile bool phase1Garuda;
    private volatile bool phase2Dancing;

    public string Status { get; }

    public VfxSwapController(IGameInteropProvider interop)
    {
        var errors = string.Empty;
        try
        {
            actorHook = interop.HookFromSignature<ActorVfxCreateDelegate>(ActorVfxCreateSignature, OnActorVfxCreate);
            actorHook.Enable();
        }
        catch (Exception ex)
        {
            actorHook?.Dispose();
            errors = $" Actor VFX hook unavailable: {ex.Message}";
        }

        try
        {
            staticHook = interop.HookFromAddress<VfxObject.Delegates.Create>(
                (nint)VfxObject.MemberFunctionPointers.Create, OnStaticVfxCreate);
            staticHook.Enable();
        }
        catch (Exception ex)
        {
            staticHook?.Dispose();
            errors += $" Static VFX hook unavailable: {ex.Message}";
        }

        Status = errors;
    }

    public void Update(bool inDuty, AudioPhase phase, bool garuda, bool dancingGreen)
    {
        phase1Garuda = inDuty && phase == AudioPhase.Phase1 && garuda;
        phase2Dancing = inDuty && phase == AudioPhase.Phase2 && dancingGreen;
    }

    private string MapPath(string path)
    {
        if (phase1Garuda && string.Equals(path, P1Fire, StringComparison.OrdinalIgnoreCase))
            return GarudaSlipstream;
        if (!phase2Dancing)
            return path;

        // These paths belong to the two 180-degree All Things Ending timelines.
        // A missing VFX resource suppresses the visual without changing the action.
        // This is the same empty-path approach used by EasyEyes.
        if (string.Equals(path, DancingCleaveLeft0, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DancingCleaveLeft1, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DancingCleaveRight, StringComparison.OrdinalIgnoreCase))
            return HiddenVfx;

        // In this encounter, the replacement model resolves Ultimate Embrace to
        // mon_sp009 (Drop the Needle), Future/Past to mon_sp004 (Deep Cut), and
        // Forsaken to mon_sp030. Exchange only their VFX, not their timelines.
        if (string.Equals(path, DancingDropTheNeedle, StringComparison.OrdinalIgnoreCase))
            return DancingDeepCut0;
        if (string.Equals(path, DancingDeepCut0, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DancingForsaken0, StringComparison.OrdinalIgnoreCase))
            return DancingDropTheNeedle;
        if (string.Equals(path, DancingDeepCut1, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DancingForsaken1, StringComparison.OrdinalIgnoreCase))
            return HiddenVfx;
        return path;
    }

    private VfxObject* OnActorVfxCreate(string path, nint caster, nint target, float duration,
        char unknown1, ushort unknown2, char unknown3)
    {
        var mapped = MapPath(path);
        return actorHook!.Original(mapped, caster, target, duration, unknown1, unknown2, unknown3);
    }

    private VfxObject* OnStaticVfxCreate(CStringPointer path, CStringPointer pool)
    {
        var mapped = MapPath(path.ToString());
        if (string.Equals(mapped, path.ToString(), StringComparison.Ordinal))
            return staticHook!.Original(path, pool);
        using var replacement = new Utf8String(mapped);
        return staticHook!.Original(replacement.StringPtr, pool);
    }

    public void Dispose()
    {
        phase1Garuda = false;
        phase2Dancing = false;
        actorHook?.Disable();
        staticHook?.Disable();
        actorHook?.Dispose();
        staticHook?.Dispose();
    }
}
