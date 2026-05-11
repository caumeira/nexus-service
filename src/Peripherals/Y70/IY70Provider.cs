namespace Qos.Service.Peripherals.Y70;

public interface IY70Provider
{
    bool IsConnected();
    string GetOrientation();
    void SetOrientation(string orientation);
    int GetBrightness();
    void SetBrightness(int brightness);
    bool GetToggle();
    void SetToggle(bool toggle);
    bool IsRotated();
}
