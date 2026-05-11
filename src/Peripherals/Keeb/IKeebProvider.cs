using Qos.Service.Models.Peripherals.Keeb;

namespace Qos.Service.Peripherals.Keeb;

public interface IKeebProvider
{
    KeyboardState GetState();
    GetKeebSettingsResponse GetSettings();
    string[] GetRotaryFunctions();
    void SetRotary(SetRotaryWheelsBody body);
    void SetRotarySensitivity(string sensitivity);
    void SetKeyReactive(SetFirmwareLightingBody body);
    void SetFirmwareLighting(SetFirmwareLightingBody body);
    void SetGameMode(SetGameModeBody body);
    KeebMacro GetMacro(int index);
    KeebMacro SetMacro(int index, SetMacroBody body);
}

public interface IInputterProvider
{
    void Send(InputterBody body);
}
