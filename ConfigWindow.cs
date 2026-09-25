using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DMUModelScale;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;

    public ConfigWindow(Configuration config, Action save) : base("Sm0ll Chaos & Exdeath###DMUModelScale")
    {
        this.config = config;
        this.save = save;
        Size = new Vector2(430, 205);
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
        if (ImGui.SliderFloat("Chaos", ref chaos, 0.30f, 1.00f, "%.2f"))
        {
            config.ChaosScale = chaos;
            save();
        }

        var exdeath = config.ExdeathScale;
        if (ImGui.SliderFloat("Exdeath", ref exdeath, 0.30f, 1.00f, "%.2f"))
        {
            config.ExdeathScale = exdeath;
            save();
        }

        ImGui.TextWrapped("Changes the size of Chaos and Exdeath as they can be extremely distracting in this fight.");

        var moveLifebar = config.MoveLifebarWithModel;
        if (ImGui.Checkbox("Move floating lifebar with model", ref moveLifebar))
        {
            config.MoveLifebarWithModel = moveLifebar;
            save();
        }
    }
}
