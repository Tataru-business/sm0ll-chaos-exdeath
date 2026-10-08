using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DMUModelScale;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Func<string> audioStatus;
    private readonly Func<string> modelStatus;

    public ConfigWindow(Configuration config, Action save, Func<string> audioStatus, Func<string> modelStatus) : base("Sm0ll Chaos & Exdeath###DMUModelScale")
    {
        this.config = config;
        this.save = save;
        this.audioStatus = audioStatus;
        this.modelStatus = modelStatus;
        Size = new Vector2(620, 430);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 430),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable in Dancing Mad (Ultimate)", ref enabled))
        {
            config.Enabled = enabled;
            save();
        }

        var chaos = config.ChaosScale;
        ImGui.BeginDisabled(config.ChaoticMode);
        if (ImGui.SliderFloat("Chaos", ref chaos, 0.30f, 1.00f, "%.2f"))
        {
            config.ChaosScale = chaos;
            save();
        }
        ImGui.EndDisabled();

        var exdeath = config.ExdeathScale;
        if (ImGui.SliderFloat("Exdeath", ref exdeath, 0.30f, 1.00f, "%.2f"))
        {
            config.ExdeathScale = exdeath;
            save();
        }

        var chaoticMode = config.ChaoticMode;
        if (ImGui.Checkbox("Chaotic mode", ref chaoticMode))
        {
            config.ChaoticMode = chaoticMode;
            save();
        }

        ImGui.TextWrapped("Changes the size of Chaos and Exdeath as they can be extremely distracting in this fight.");
        ImGui.TextWrapped("Chaotic mode: Chaos is smallest while idle and full size while casting.");

        ImGui.Separator();
        var brainrot = config.Brainrot;
        if (ImGui.Checkbox("Brainrot", ref brainrot))
        {
            config.Brainrot = brainrot;
            save();
        }
        ImGui.TextWrapped("P1 Garuda and P2 Dancing Green models, names, clones and effects; phase music and P3 Bonk slams.");
        ImGui.TextWrapped("Boss hitboxes and mechanics stay the same.");
        ImGui.TextWrapped(modelStatus());
        ImGui.TextWrapped("Phase 1: Fallen Angel | Phase 2: Ride the Rhythm | Phase 3+: Circus. Each track loops until its phase ends.");
        ImGui.TextWrapped("Music follows the game's BGM and master volume sliders and pauses when BGM is off.");
        ImGui.TextWrapped("All four audio clips are included. Audio only plays in Dancing Mad (Ultimate).");
        ImGui.TextWrapped(audioStatus());

        var moveLifebar = config.MoveLifebarWithModel;
        if (ImGui.Checkbox("Move floating lifebar with model", ref moveLifebar))
        {
            config.MoveLifebarWithModel = moveLifebar;
            save();
        }
    }
}
