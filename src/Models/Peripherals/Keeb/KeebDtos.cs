using System.Collections.Generic;
using Nexus.Service.Models.Common;

namespace Nexus.Service.Models.Peripherals.Keeb;

public class KeyboardState
{
    public bool IsConnected { get; set; }
    public int Profile { get; set; }
    public int Layer { get; set; }
    public string Layout { get; set; } = "ANSI";
    public List<List<KeebKey>> Keys { get; set; } = new();
}

public class KeebKey
{
    public string Mode { get; set; } = "";
    public string Function { get; set; } = "";
    public int? Input { get; set; }
}

public class GetRotaryFunctionsResponse : ApiResponse
{
    public string[] Functions { get; set; } = System.Array.Empty<string>();
}

public class SetRotaryWheelsBody
{
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
    public List<RotaryAppOverride> Apps { get; set; } = new();
}

public class RotaryAppOverride
{
    public string TargetId { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
}

public class SetRotarySensitivityBody
{
    public string Sensitivity { get; set; } = "Balanced";
}

public class GetKeebSettingsResponse : ApiResponse
{
    public bool ShiftKeyDisabled { get; set; }
    public bool WindowsKeyDisabled { get; set; }
    public bool AltF4Disabled { get; set; }
    public bool AltTabDisabled { get; set; }
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
    public bool KeyIndicator { get; set; }
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
}

public class SetPassiveLightingBody
{
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
}

public class SetFirmwareLightingBody
{
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
    public bool KeyIndicator { get; set; }
}

public class SetGameModeBody
{
    public bool AltF4 { get; set; }
    public bool AltTab { get; set; }
    public bool ShiftTab { get; set; }
    public bool WindowsKey { get; set; }
}

public class GetMacroResponse : ApiResponse
{
    public KeebMacro Macro { get; set; } = new();
}

public class KeebMacro
{
    public int Index { get; set; }
    public List<MacroKey> Keys { get; set; } = new();
}

public class MacroKey
{
    public string Key { get; set; } = "";
    public int Duration { get; set; }
    public string Type { get; set; } = "KeyDown";
    public string Category { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
}

public class SetMacroBody
{
    public List<MacroKey> Keys { get; set; } = new();
}

public class InputterBody
{
    public List<MacroStroke> Strokes { get; set; } = new();
}

public class MacroStroke
{
    public string Key { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public int Duration { get; set; }
    public string Type { get; set; } = "keydown";
}
