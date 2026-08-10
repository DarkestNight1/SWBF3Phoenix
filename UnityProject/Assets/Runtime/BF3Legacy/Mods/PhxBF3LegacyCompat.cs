using UnityEngine;

/// <summary>
/// Lua compatibility shim for the BF3 Legacy 3.1 pack (and any other addon
/// built against [GT]Anakin's SWBF2 UI Remaster).
///
/// Every addme.script in the pack calls two helpers that do not exist in stock
/// Battlefront II:
///
///   MergeTables(dst, src)   - deep-merges mission list tables, so a component
///                             can add modes to a map another component owns
///                             (MoreMaps adding Hero Deathmatch to Coruscant).
///   AddNewGameModes(...)    - declares the display name / blurb / icon of game
///                             modes and eras the shell does not ship with,
///                             i.e. Orbital Assault and the two BF3 eras.
///
/// In the original engine both come from the UI Remaster's patched shell
/// scripts, which is why the pack lists it as a hard requirement. Phoenix
/// reimplements the shell in C#, so the Remaster's version is never loaded even
/// when the user has it installed - without this shim every addme.script in the
/// pack dies on its first call and NOT ONE BF3 Legacy map reaches the menu.
///
/// The declarations are forwarded to PhxBF3LegacyContent, which the main menu
/// consults when expanding a map's modes and eras.
///
/// Both helpers are installed only if undefined, so a real Remaster-provided
/// implementation (should one ever reach this Lua state) keeps precedence.
/// </summary>
public static class PhxBF3LegacyCompat
{
    static bool Installed;

    /// <summary>
    /// AddNewGameModes' argument shape is not documented anywhere and differs
    /// between pack components - some pass a flat table of descriptors, some
    /// wrap them in a 'change'/'add' section, some pass an id string first. So
    /// rather than assume one shape, walk whatever comes in and pick up every
    /// table keyed by a 'mode_*' or 'era_*' name. Unknown shapes degrade to
    /// "no descriptors found" instead of a Lua error.
    /// </summary>
    const string Prelude = @"
if MergeTables == nil then
    function MergeTables(dst, src)
        if type(dst) ~= 'table' then return src end
        if type(src) ~= 'table' then return dst end
        for k, v in pairs(src) do
            if type(v) == 'table' and type(dst[k]) == 'table' then
                MergeTables(dst[k], v)
            else
                dst[k] = v
            end
        end
        return dst
    end
end

if AddNewGameModes == nil then
    -- the C# side takes four strings; anything else must not reach it
    function phx_bf3_str(v)
        if type(v) == 'string' then return v end
        if type(v) == 'number' then return tostring(v) end
        return ''
    end

    function phx_bf3_collect_modes(t, depth)
        if type(t) ~= 'table' or depth > 4 then return end
        for k, v in pairs(t) do
            if type(v) == 'table' then
                local isMode = (type(k) == 'string') and
                               (string.sub(k, 1, 5) == 'mode_' or string.sub(k, 1, 4) == 'era_')
                if isMode then
                    local icon = v.icon2
                    if icon == nil then icon = v.icon end
                    PhxBF3RegisterGameMode(k,
                        phx_bf3_str(v.name),
                        phx_bf3_str(v.about),
                        phx_bf3_str(icon))
                end
                -- a descriptor may still nest overrides below it
                phx_bf3_collect_modes(v, depth + 1)
            end
        end
    end

    function AddNewGameModes(...)
        for i = 1, table.getn(arg) do
            phx_bf3_collect_modes(arg[i], 0)
        end
    end
end
";

    /// <summary>
    /// Install into the given Lua state. Safe to call repeatedly; the Lua state
    /// is rebuilt per environment, so this runs once per environment.
    /// </summary>
    public static void Install(PhxLuaRuntime runtime)
    {
        if (runtime == null) return;

        if (!runtime.ExecuteString(Prelude))
        {
            Debug.LogWarning("[BF3Legacy] Failed to install the addon Lua compatibility shim - " +
                             "mods built against the SWBF2 UI Remaster (incl. BF3 Legacy) " +
                             "may not register their maps.");
            return;
        }

        if (!Installed)
        {
            Installed = true;
            Debug.Log("[BF3Legacy] Addon Lua compatibility shim installed " +
                      "(MergeTables, AddNewGameModes)");
        }
    }
}
