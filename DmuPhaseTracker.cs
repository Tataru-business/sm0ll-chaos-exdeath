using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DMUModelScale;

internal enum AudioPhase
{
    Outside,
    Phase1,
    Phase2,
    Phase3Onward
}

/// <summary>Tracks the fight's music phases from its visible battle actors.</summary>
internal sealed class DmuPhaseTracker(IObjectTable objectTable)
{
    private const uint KefkaP2BaseId = 19506;
    private const uint ChaosP3BaseId = 19508;
    private const uint ExdeathP3BaseId = 19509;

    private AudioPhase phase = AudioPhase.Phase1;
    private bool sawP2Targetable;

    public void ResetForNewPull()
    {
        phase = AudioPhase.Phase1;
        sawP2Targetable = false;
    }

    public AudioPhase Update(bool inDuty)
    {
        if (!inDuty)
        {
            ResetForNewPull();
            return AudioPhase.Outside;
        }

        var p2Present = false;
        var p2Targetable = false;
        var p2Damaged = false;
        var p3Present = false;
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc)
                continue;

            if (obj.BaseId is ChaosP3BaseId or ExdeathP3BaseId)
            {
                p3Present = true;
                continue;
            }

            if (obj.BaseId != KefkaP2BaseId)
                continue;

            p2Present = true;
            p2Targetable |= obj.IsTargetable;
            if (obj is IBattleChara battle && battle.MaxHp > 0)
                p2Damaged |= battle.CurrentHp < battle.MaxHp;
        }

        if (p3Present)
            phase = AudioPhase.Phase3Onward;
        else if (phase == AudioPhase.Phase1 && p2Present)
            phase = AudioPhase.Phase2;

        if (phase == AudioPhase.Phase2)
        {
            sawP2Targetable |= p2Targetable;
            // P2 ends as the damaged boss becomes untargetable; some clients
            // remove the actor in the same frame instead.
            if ((p2Present && !p2Targetable && p2Damaged) ||
                (sawP2Targetable && !p2Present))
                phase = AudioPhase.Phase3Onward;
        }

        return phase;
    }
}
