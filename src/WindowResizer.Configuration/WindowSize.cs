using System;
using Newtonsoft.Json;
using WindowResizer.Common.Utils;
using WindowResizer.Common.Windows;

namespace WindowResizer.Configuration;

public class WindowSize
{
    public string WindowSizeId { get; set; } = ConfigHelper.GenerateConfigId();

    public string Name { get; set; } = String.Empty;

    public string Title { get; set; } = String.Empty;

    public Rect Rect { get; set; }

    public WindowState State { get; set; } = WindowState.Normal;

    public Point MaximizedPosition { get; set; } = new(0, 0);

    public bool AutoResize { get; set; }

    // AutoResize Delay Milliseconds
    public int AutoResizeDelay { get; set; }

    #region properties

    [JsonIgnore]
    public int Top
    {
        get { return Rect.Top; }
        set
        {
            Rect = Rect with { Top = value };
        }
    }

    [JsonIgnore]
    public int Left
    {
        get { return Rect.Left; }
        set
        {
            Rect = Rect with { Left = value };
        }
    }

    [JsonIgnore]
    public int Right
    {
        get { return Rect.Right; }
        set
        {
            Rect = Rect with { Right = value };
        }
    }

    [JsonIgnore]
    public int Bottom
    {
        get { return Rect.Bottom; }
        set
        {
            Rect = Rect with { Bottom = value };
        }
    }

    #endregion
}
