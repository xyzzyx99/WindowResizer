using System;

namespace WindowResizer.Common.Utils;

public class ConfigHelper
{
    public static string GenerateConfigId() =>
        Guid.NewGuid().ToString("N");
}
