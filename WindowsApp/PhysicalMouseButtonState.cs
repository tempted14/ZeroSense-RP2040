using System;
using System.Collections.Generic;
using System.Linq;

namespace RainbowRecoil;

/// <summary>
/// Tracks buttons per raw-input mouse. A click released by the RP2040's second
/// HID interface must not cancel a trigger still held on the physical mouse.
/// </summary>
internal sealed class PhysicalMouseButtonState
{
    private readonly Dictionary<nint, byte> _buttons = new();

    public bool AimAndFireHeld => _buttons.Values.Any(value => (value & 3) == 3);

    public void Apply(nint device, ushort flags)
    {
        if (device == 0)
        {
            return;
        }

        _buttons.TryGetValue(device, out var buttons);
        if ((flags & 0x0001) != 0) buttons |= 1; // RI_MOUSE_LEFT_BUTTON_DOWN
        if ((flags & 0x0002) != 0) buttons &= 0xFE; // RI_MOUSE_LEFT_BUTTON_UP
        if ((flags & 0x0004) != 0) buttons |= 2; // RI_MOUSE_RIGHT_BUTTON_DOWN
        if ((flags & 0x0008) != 0) buttons &= 0xFD; // RI_MOUSE_RIGHT_BUTTON_UP
        if (buttons == 0)
        {
            _buttons.Remove(device);
        }
        else
        {
            _buttons[device] = buttons;
        }
    }

    public void Remove(nint device) => _buttons.Remove(device);

    public void Clear() => _buttons.Clear();
}
