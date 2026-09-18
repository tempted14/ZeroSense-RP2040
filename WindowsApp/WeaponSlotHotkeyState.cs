namespace RainbowRecoil;

public readonly record struct WeaponSlotKeyEdges(bool PrimaryPressed, bool SecondaryPressed);

/// <summary>Edge detector for the global number-row loadout shortcuts.</summary>
public sealed class WeaponSlotHotkeyState
{
    private bool _primaryWasPressed;
    private bool _secondaryWasPressed;

    public WeaponSlotKeyEdges Update(bool primaryPressed, bool secondaryPressed)
    {
        var edges = new WeaponSlotKeyEdges(
            primaryPressed && !_primaryWasPressed,
            secondaryPressed && !_secondaryWasPressed);
        _primaryWasPressed = primaryPressed;
        _secondaryWasPressed = secondaryPressed;
        return edges;
    }

    public void Reset()
    {
        _primaryWasPressed = false;
        _secondaryWasPressed = false;
    }
}
