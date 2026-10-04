using Dalamud.Configuration;
using System;
using DMUP3BlackholeHelper;

namespace DMUP4DebuffHelper;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;

    // Retained for migrating configurations saved before the instance-only option.
    public bool ShowHelper { get; set; } = true;

    public bool OnlyShowInInstance { get; set; }

    public bool PreviewWhenInactive { get; set; }

    public bool HelperCollapsed { get; set; }

    public bool EnableP3Tracking { get; set; } = true;

    public bool EnableP4Tracking { get; set; } = true;

    public bool EnableFloodTracking { get; set; } = true;

    public FloodDestinationNaming FloodDestinationNaming { get; set; } = FloodDestinationNaming.GameNames;

    public bool ShowStackSpreadTiming { get; set; }

    public bool ShowP4RealityLabels { get; set; } = true;

    public bool UseMotionStillnessLabels { get; set; }

    public GazeDirectionNaming GazeDirectionNaming { get; set; } = GazeDirectionNaming.AwayToward;

    public BlackHoleStrategyKind SelectedBlackHoleStrategy { get; set; } = BlackHoleStrategyKind.Standard;

    public float HelperFontScale { get; set; } = 1.0f;

    public float HelperIconScale { get; set; } = 1.0f;

    public float HelperBackgroundOpacity { get; set; } = 1.0f;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
