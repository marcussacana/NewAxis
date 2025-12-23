using System;

namespace NewAxis.Models;

public enum ModType
{
    ThreeDPlus,
    ThreeDUltra,
    Native
}

public static class ModTypeExtensions
{
    public static string GetDescription(this ModType value)
    {
        return value switch
        {
            ModType.ThreeDPlus => "3D+",
            ModType.ThreeDUltra => "3D Ultra",
            ModType.Native => "Native",
            _ => value.ToString()
        };
    }

    public static ModType? FromDescription(string description)
    {
        return description switch
        {
            "3D+" => ModType.ThreeDPlus,
            "3D Ultra" => ModType.ThreeDUltra,
            "Native" => ModType.Native,
            _ => null
        };
    }
}
