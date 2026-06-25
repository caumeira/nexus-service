using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Models.Peripherals.Keeb;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// Windows keyboard input injection via SendInput P/Invoke.
/// AOT-safe - no NuGet packages, no WinRT/COM dependencies.
/// </summary>
public sealed class WindowsInputter : IInputterProvider
{
    public void Send(InputterBody body)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (body.Strokes == null || body.Strokes.Count == 0) return;

        foreach (var stroke in body.Strokes)
        {
            var vk = ParseKey(stroke.Key);
            if (vk == 0) continue;

            bool isDown = string.Equals(stroke.Type, "keydown", StringComparison.OrdinalIgnoreCase);

            // Press modifiers
            if (stroke.Ctrl) SendKey(VK_CONTROL, true);
            if (stroke.Shift) SendKey(VK_SHIFT, true);
            if (stroke.Alt) SendKey(VK_MENU, true);
            if (stroke.Meta) SendKey(VK_LWIN, true);

            // Key event
            SendKey(vk, isDown);

            if (stroke.Duration > 0)
                Thread.Sleep(stroke.Duration);

            // Release modifiers (reverse order)
            if (stroke.Meta) SendKey(VK_LWIN, false);
            if (stroke.Alt) SendKey(VK_MENU, false);
            if (stroke.Shift) SendKey(VK_SHIFT, false);
            if (stroke.Ctrl) SendKey(VK_CONTROL, false);
        }
    }

    private static void SendKey(ushort vk, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wVk = vk;
        input.ki.dwFlags = down ? 0u : KEYEVENTF_KEYUP;
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private static ushort ParseKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;

        // Letters: KeyA..KeyZ
        if (key.StartsWith("Key", StringComparison.Ordinal) && key.Length == 4)
            return (ushort)key[3]; // A-Z map to their ASCII values

        // Digits: Digit0..Digit9
        if (key.StartsWith("Digit", StringComparison.Ordinal) && key.Length == 6)
            return (ushort)('0' + (key[5] - '0'));

        // Function keys: F1..F24
        if (key.StartsWith('F') && key.Length >= 2 && key.Length <= 3
            && int.TryParse(key.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
        {
            return (ushort)(0x70 + fn - 1); // VK_F1 = 0x70
        }

        return key switch
        {
            "Space" => 0x20,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "ArrowUp" => 0x26,
            "ArrowDown" => 0x28,
            "ArrowLeft" => 0x25,
            "ArrowRight" => 0x27,
            "CapsLock" => 0x14,
            "NumLock" => 0x90,
            "ScrollLock" => 0x91,
            "PrintScreen" => 0x2C,
            "Pause" => 0x13,
            "ContextMenu" => 0x5D,
            "MediaPlayPause" => 0xB3,
            "MediaStop" => 0xB2,
            "MediaTrackNext" => 0xB0,
            "MediaTrackPrevious" => 0xB1,
            "AudioVolumeMute" => 0xAD,
            "AudioVolumeDown" => 0xAE,
            "AudioVolumeUp" => 0xAF,
            _ => 0,
        };
    }

    // P/Invoke constants
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
        private readonly IntPtr _pad1, _pad2; // union padding to match MOUSEINPUT size
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);
}
