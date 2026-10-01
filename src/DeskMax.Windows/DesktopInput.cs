using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskMax.Contracts;

namespace DeskMax.Windows;

public sealed class DesktopInput
{
    private readonly object Gate = new();
    private readonly HashSet<int> Keys = [];
    private readonly HashSet<string> Buttons = [];

    public void Apply(RemoteInputMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (Gate)
        {
            switch (message.Kind)
            {
                case "text":
                    if (message.Text is not { Length: > 0 and <= 1024 } || message.Text.Contains('\0')) return;
                    // UTF-16 code units preserve non-Latin text and surrogate pairs in Windows input.
                    var unicode = new List<Input>();
                    foreach (char character in message.Text)
                    {
                        unicode.Add(new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 4 } } });
                        unicode.Add(new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 6 } } });
                    }
                    Send(unicode.ToArray());
                    break;
                case "move":
                    if (!double.IsFinite(message.X) || !double.IsFinite(message.Y) || message.X is < 0 or > 1 || message.Y is < 0 or > 1) return;
                    Send(Mouse(0x8001, (int)Math.Round(message.X * 65535), (int)Math.Round(message.Y * 65535)));
                    break;
                case "wheel":
                    if (message.Delta is < -1200 or > 1200 || message.Delta == 0) return;
                    if (!ValidPosition(message)) return;
                    Send(Position(message), Mouse(0x800, data: unchecked((uint)message.Delta)));
                    break;
                case "releaseAll":
                    ReleaseAll();
                    break;
                case "down":
                case "up":
                    if (message.Button is not ("left" or "right" or "middle")) return;
                    if (!ValidPosition(message)) return;
                    bool down = message.Kind == "down";
                    if (down ? Buttons.Contains(message.Button) : !Buttons.Contains(message.Button)) return;
                    Send(Position(message), Button(message.Button, down));
                    if (down) Buttons.Add(message.Button); else Buttons.Remove(message.Button);
                    break;
                case "keyDown":
                case "keyUp":
                    if (message.Key is < 1 or > 255) return;
                    bool pressed = message.Kind == "keyDown";
                    if (!pressed && !Keys.Contains(message.Key)) return;
                    Send(Keyboard(message.Key, pressed));
                    if (pressed) Keys.Add(message.Key); else Keys.Remove(message.Key);
                    break;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (Gate)
        {
            // Best effort for every held input even when Windows rejects injection on a secure desktop.
            foreach (int key in Keys.ToArray())
                if (TrySend(Keyboard(key, false))) Keys.Remove(key);
            foreach (string button in Buttons.ToArray())
                if (TrySend(Button(button, false))) Buttons.Remove(button);
        }
    }

    private static Input Button(string button, bool down) => Mouse(button switch
    {
        "left" => down ? 0x2u : 0x4u,
        "right" => down ? 0x8u : 0x10u,
        _ => down ? 0x20u : 0x40u
    });

    private static Input Mouse(uint flags, int x = 0, int y = 0, uint data = 0) => new()
    {
        Type = 0, Union = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags } }
    };

    private static bool ValidPosition(RemoteInputMessage message) => double.IsFinite(message.X) && double.IsFinite(message.Y) && message.X is >= 0 and <= 1 && message.Y is >= 0 and <= 1;
    private static Input Position(RemoteInputMessage message) => Mouse(0x8001, (int)Math.Round(message.X * 65535), (int)Math.Round(message.Y * 65535));

    private static Input Keyboard(int key, bool down)
    {
        uint scan = MapVirtualKey((uint)key, 4);
        bool extended = (scan & 0xFF00) == 0xE000;
        return new Input
        {
            Type = 1,
            Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Scan = (ushort)(scan & 255), Flags = (extended ? 1u : 0u) | (down ? 0u : 2u) } }
        };
    }

    private static void Send(params Input[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected remote input.");
    }
    private static bool TrySend(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
