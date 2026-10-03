using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteDeck;

public sealed record ControlMessage(
    string Type, int Dx = 0, int Dy = 0, int Delta = 0,
    string? Button = null, string? State = null, string? Key = null,
    string? Value = null, string? Action = null);

public sealed class WindowsInputService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, ushort> Keys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D,
            ["escape"] = 0x1B, ["space"] = 0x20, ["pageUp"] = 0x21,
            ["pageDown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27,
            ["down"] = 0x28, ["delete"] = 0x2E
        };
    private static readonly IReadOnlyDictionary<string, ushort> MediaKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["next"] = 0xB0, ["previous"] = 0xB1, ["stop"] = 0xB2,
            ["playPause"] = 0xB3, ["mute"] = 0xAD, ["volumeDown"] = 0xAE,
            ["volumeUp"] = 0xAF
        };

    public void Execute(ControlMessage message)
    {
        lock (_gate)
        {
            switch (message.Type)
            {
                case "move": Move(Math.Clamp(message.Dx, -250, 250), Math.Clamp(message.Dy, -250, 250)); break;
                case "click": Click(message.Button ?? "left"); break;
                case "button": SetButton(message.Button ?? "left", message.State ?? "up"); break;
                case "scroll": Scroll(Math.Clamp(message.Delta, -1200, 1200)); break;
                case "key": PressNamedKey(message.Key ?? ""); break;
                case "text": TypeText(message.Value ?? ""); break;
                case "media": PressMediaKey(message.Action ?? ""); break;
                default: throw new InvalidOperationException("Unknown command type.");
            }
        }
    }

    public void ReleaseButtons()
    {
        lock (_gate)
        {
            if (_heldButtons.Count == 0)
                return;

            var releases = _heldButtons
                .Select(button => MouseInput(GetButtonFlag(button, down: false)))
                .ToArray();

            try { Send(releases); }
            finally { _heldButtons.Clear(); }
        }
    }

    private static void Move(int dx, int dy) => Send(MouseInput(MOUSEEVENTF_MOVE, dx, dy));
    private static void Scroll(int delta) => Send(MouseInput(MOUSEEVENTF_WHEEL, mouseData: unchecked((uint)delta)));
    private static void Click(string button)
    {
        var (down, up) = button.ToLowerInvariant() switch
        {
            "left" => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => throw new InvalidOperationException("Unknown mouse button.")
        };
        Send(MouseInput(down), MouseInput(up));
    }
    private void SetButton(string button, string state)
    {
        var normalizedButton = button.ToLowerInvariant();
        var down = state.Equals("down", StringComparison.OrdinalIgnoreCase);

        if (down && _heldButtons.Contains(normalizedButton))
            return;

        if (!down && !_heldButtons.Contains(normalizedButton))
            return;

        Send(MouseInput(GetButtonFlag(normalizedButton, down)));

        if (down)
            _heldButtons.Add(normalizedButton);
        else
            _heldButtons.Remove(normalizedButton);
    }
    private static uint GetButtonFlag(string button, bool down)
    {
        return button switch
        {
            "left" => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => throw new InvalidOperationException("Unknown mouse button.")
        };
    }
    private static void PressNamedKey(string name) { if (!Keys.TryGetValue(name, out var key)) throw new InvalidOperationException("Key is not allowed."); PressVirtualKey(key); }
    private static void PressMediaKey(string action) { if (!MediaKeys.TryGetValue(action, out var key)) throw new InvalidOperationException("Media action is not allowed."); PressVirtualKey(key); }
    private static void PressVirtualKey(ushort key) => Send(KeyboardInput(key, 0), KeyboardInput(key, KEYEVENTF_KEYUP));
    private static void TypeText(string text)
    {
        foreach (var character in text[..Math.Min(text.Length, 1000)])
            Send(UnicodeInput(character, KEYEVENTF_UNICODE), UnicodeInput(character, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
    }
    private static INPUT MouseInput(uint flags, int dx = 0, int dy = 0, uint mouseData = 0) => new()
    {
        type = INPUT_MOUSE, Data = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags } }
    };
    private static INPUT KeyboardInput(ushort key, uint flags) => new()
    {
        type = INPUT_KEYBOARD, Data = new INPUTUNION { ki = new KEYBDINPUT { wVk = key, dwFlags = flags } }
    };
    private static INPUT UnicodeInput(char character, uint flags) => new()
    {
        type = INPUT_KEYBOARD, Data = new INPUTUNION { ki = new KEYBDINPUT { wScan = character, dwFlags = flags } }
    };
    private static void Send(params INPUT[] inputs)
    {
        var sent = NativeSendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    private const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4,
        MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 16, MOUSEEVENTF_MIDDLEDOWN = 32,
        MOUSEEVENTF_MIDDLEUP = 64, MOUSEEVENTF_WHEEL = 0x800;
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint NativeSendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
}
