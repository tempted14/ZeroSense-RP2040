using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RainbowRecoil;

/// <summary>Operator and loadout data projected from the current weapon catalog.</summary>
public sealed class OperatorSelectorViewModel
{
    public ObservableCollection<OperatorViewModel> AllOperators { get; } = new(
        SiegeCatalog.Operators.Select(definition => new OperatorViewModel(
            new OperatorData(definition.Name, definition.PrimaryWeapon, definition.Weapons))));

    public List<WeaponProfileViewModel> AllProfiles { get; } = WeaponProfile.LoadAll()
        .Select(profile => new WeaponProfileViewModel(profile))
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public List<string> GetOperatorNames() => AllOperators
        .Select(item => item.OperatorData.Name)
        .ToList();
}

public sealed class OperatorData
{
    public string Name { get; }
    public string PrimaryWeapon { get; }
    public IReadOnlyList<string> Weapons { get; }

    public OperatorData(string name, string primaryWeapon, IReadOnlyList<string> weapons)
    {
        Name = name;
        PrimaryWeapon = primaryWeapon;
        Weapons = weapons;
    }
}

public sealed class OperatorViewModel
{
    public OperatorData OperatorData { get; }
    public string OperatorName => OperatorData.Name;
    public string PrimaryWeaponName => OperatorData.PrimaryWeapon;
    public string LoadoutSummary => string.Join(", ", OperatorData.Weapons);

    public OperatorViewModel(OperatorData data)
    {
        OperatorData = data;
    }

    public override string ToString() => $"{OperatorName} ({PrimaryWeaponName})";
}
