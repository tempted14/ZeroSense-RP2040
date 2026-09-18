using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RainbowRecoil;

/// <summary>
/// Current Rainbow Six Siege weapon/operator catalog.
/// Names, categories, and operator assignments were checked against the public
/// R6 weapon catalog and the Y11S3 community-maintained weapon-statistics roster
/// on 2026-09-15. Compensation values are deliberately kept outside this catalog.
/// </summary>
public static class SiegeCatalog
{
    private const string CatalogData = """
L85A2|Assault Rifle|Sledge,Thatcher
AR33|Assault Rifle|Thatcher,Flores
G36C|Assault Rifle|Ash,Iana
R4-C|Assault Rifle|Ash,Ram
556XI|Assault Rifle|Thermite,Osa
F2|Assault Rifle|Twitch,Solid Snake
AK-12|Assault Rifle|Fuze,Ace
552 COMMANDO|Assault Rifle|IQ,Grim
AUG A2|Assault Rifle|IQ,Wamai
C8-SFW|Assault Rifle|Buck
MK17 CQB|Assault Rifle|Blackbeard
PARA-308|Assault Rifle|Capitão,Brava
TYPE-89|Assault Rifle|Hibana
C7E|Assault Rifle|Jackal
M762|Assault Rifle|Zofia
V308|Assault Rifle|Lion
SPEAR.308|Assault Rifle|Finka,Thunderbird
M4|Assault Rifle|Maverick,Striker
ARX200|Assault Rifle|Nomad,Iana
AK-74M|Assault Rifle|Nomad,Deimos
F90|Assault Rifle|Gridlock
SC3000K|Assault Rifle|Zero
POF-9|Assault Rifle|Sens
416-C CARBINE|Assault Rifle|Jäger
AUG A3|SMG|Kaid
6P41|LMG|Fuze,Finka
G8A1|LMG|IQ,Amaru
M249|LMG|Capitão,Striker,Rauora
M249 SAW|LMG|Gridlock
T-95 LSW|LMG|Ying,Flores
LMG-E|LMG|Zofia,Ram
DP27|LMG|Tachanka
ALDA 5.56|LMG|Maestro,Noor
417|Marksman Rifle|Twitch,Lion,Sens,Rauora
CAMRS|Marksman Rifle|Buck,Brava
SR-25|Marksman Rifle|Blackbeard,Flores,Striker
Mk 14 EBR|Marksman Rifle|Dokkaebi,Aruni
AR-15.50|Marksman Rifle|Maverick,Tubarão
OTs-03|Sniper Rifle|Glaz
CSRX 300|Sniper Rifle|Kali
M590A1|Shotgun|Sledge,Thatcher,Smoke,Mute,Deimos,Warden
M1014|Shotgun|Thermite,Pulse,Castle,Ace
SG-CQB|Shotgun|Twitch,Lion,Doc,Rook,Grim
SUPERNOVA|Shotgun|Hibana,Echo,Amaru
ITA12L|Shotgun|Jackal,Mira,Solis
ITA12S|Shotgun|Jackal,Mira,Frost,Amaru,Striker,Thunderbird,Thermite
SIX12|Shotgun|Ying
SIX12 SD|Shotgun|Lesion,Nøkk
SASG-12|Shotgun|Finka,Kapkan,Fenrir
SUPER SHORTY|Shotgun|Gridlock,Castle,Brava,Clash,Sentry,Wamai
M870|Shotgun|Thorn,Jäger,Bandit,Sentry
Super 90|Shotgun|Frost,Melusi
SPAS-12|Shotgun|Valkyrie,Oryx
SPAS-15|Shotgun|Caveira,Thunderbird
FO-12|Shotgun|Ela
ACS12|Shotgun|Azami,Alibi,Maestro
BOSG.12.2|Shotgun|Dokkaebi,Vigil
TCSG12|Shotgun|Kaid,Goyo,Sentry
PDW9|SMG|Jackal,Osa
FMG-9|SMG|Smoke,Nøkk,Denari
SMG-11|Machine Pistol|Smoke,Mute,Amaru,Solis
MP7|SMG|Bandit,Zero,Fenrir
MP5K|SMG|Mute,Wamai
UMP45|SMG|Castle,Pulse
MP5|SMG|Rook,Doc,Melusi
P90|SMG|Rook,Doc,Solis
9x19VSN|SMG|Kapkan,Azami,Tachanka
9mm C1|SMG|Frost
MPX|SMG|Valkyrie,Warden,Tubarão
M12|SMG|Caveira
MP5SD|SMG|Echo
VECTOR.45 ACP|SMG|Mira,Goyo
T-5 SMG|SMG|Lesion,Oryx
K1A|SMG|Vigil
SCORPION EVO 3 A1|SMG|Ela,Denari
Mx4 Storm|SMG|Alibi
Commando 9|Assault Rifle|Mozzie,Sentry,Noor
P10 Roni|SMG|Mozzie,Aruni
UZK50GI|SMG|Thorn
BEARING 9|Machine Pistol|Glaz,Hibana,Echo,Tachanka,Thunderbird
SMG-12|Machine Pistol|Dokkaebi,Vigil,Warden
C75 Auto|Machine Pistol|Thorn,Vigil,Kali,Dokkaebi,Sentry
SPSMG9|Machine Pistol|Kali,Clash
P226 MK 25|Handgun|Sledge,Thatcher,Smoke,Mute,Tubarão,Kali,Denari
M45 MEUSOC|Handgun|Ash,Thermite,Pulse,Castle
5.7 USG|Handgun|Ash,Thermite,Pulse,Castle,Zero,Nøkk,Fenrir,Striker
P9|Handgun|Twitch,Rook,Doc,Montagne,Ace,Lion
PMM|Handgun|Glaz,Fuze,Kapkan,Tachanka,Finka,Osa
GSH-18|Handgun|Fuze,Kapkan,Tachanka,Finka,Flores,Rauora
P12|Handgun|Blitz,IQ,Bandit,Jäger,Wamai
MK1 9mm|Handgun|Buck,Frost,Ram,Iana
D-50|Handgun|Valkyrie,Nøkk,Azami
PRB92|Handgun|Capitão,Aruni,Nomad
P229|Handgun|Hibana,Echo,Grim,Goyo,Skopós
USP40|Handgun|Jackal,Mira,Brava,Oryx
Q-929|Handgun|Ying,Lesion,Thunderbird
RG15|Handgun|Zofia,Ela,Melusi
1911 TACOPS|Handgun|Maverick,Thorn,Noor
.44 Mag Semi-Auto|Handgun|Nomad,Kaid
SDP 9mm|Handgun|Sens,Mozzie,Gridlock
LUISON|Handgun|Caveira
P-10C|Handgun|Clash,Warden,Jäger
LFP586|Revolver|Twitch,Rook,Doc,Montagne,Kaid,Lion
Bailiff 410|Revolver|Oryx,Alibi,Grim,Maestro,Doc,Noor
KERATOS.357|Revolver|Maestro,Alibi,Wamai,Bandit
.44 Vendetta|Revolver|Deimos
BALLISTIC SHIELD|Shield|Fuze,Montagne,Blitz,Blackbeard
CCE SHIELD|Shield|Clash
GONNE-6|Hand Cannon|Glaz,Iana,Zero,Dokkaebi,Amaru,Capitão
PCX-33|Assault Rifle|Skopós
GLAIVE-12|Shotgun|Denari
REAPER-MK2|Machine Pistol|Rauora,Oryx,Pulse,Rook,Sledge,Ying
PMR90A2|Marksman Rifle|Thatcher,Capitão,Hibana,Nøkk,Solid Snake
TACIT .45|Handgun|Solid Snake
XK23|Assault Rifle|Dokkaebi,Rauora,Sens
""";

    public static ReadOnlyCollection<SiegeWeaponDefinition> Weapons { get; } =
        Array.AsReadOnly(ParseCatalog());

    public static ReadOnlyCollection<SiegeOperatorDefinition> Operators { get; } =
        Array.AsReadOnly(BuildOperators());

    private static SiegeWeaponDefinition[] ParseCatalog()
    {
        return CatalogData
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd('\r').Split('|'))
            .Select(parts => new SiegeWeaponDefinition(
                parts[0],
                parts[1],
                parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .ToArray();
    }

    private static SiegeOperatorDefinition[] BuildOperators()
    {
        var assignments = new Dictionary<string, List<SiegeWeaponDefinition>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var weapon in Weapons)
        {
            foreach (var operatorName in weapon.Operators)
            {
                if (!assignments.TryGetValue(operatorName, out var loadout))
                {
                    loadout = new List<SiegeWeaponDefinition>();
                    assignments[operatorName] = loadout;
                }

                loadout.Add(weapon);
            }
        }

        return assignments
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new SiegeOperatorDefinition(
                pair.Key,
                pair.Value.Select(weapon => weapon.Name).ToArray(),
                pair.Value.FirstOrDefault(weapon => IsPrimaryType(weapon.Type))?.Name
                    ?? pair.Value[0].Name))
            .ToArray();
    }

    private static bool IsPrimaryType(string type) => type is
        "Assault Rifle" or "SMG" or "LMG" or "Marksman Rifle" or "Sniper Rifle" or "Shotgun";
}

public sealed record SiegeWeaponDefinition(string Name, string Type, string[] Operators);

public sealed record SiegeOperatorDefinition(string Name, string[] Weapons, string PrimaryWeapon);
