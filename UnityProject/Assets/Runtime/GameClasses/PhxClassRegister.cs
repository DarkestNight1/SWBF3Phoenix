using System;
using System.Collections.Generic;
using UnityEngine;

public static class PhxClassRegister
{
    struct GameBaseClass
    {
        public Type ClassType;
        public Type InstanceType;

        public GameBaseClass(Type ct, Type it)
        {
            Debug.Assert(ct != null || it != null);

            ClassType = ct;
            InstanceType = it;
        }
    }

    // When setting either the class type or instance type to null, make sure that either noone will
    // attempt to resolve the class string to a class type, or create an instance of it, respectively.
    static Dictionary<string, GameBaseClass> TypeDB = new Dictionary<string, GameBaseClass>()
    {
        { "prop",           new GameBaseClass(typeof(PhxProp.ClassProperties),           typeof(PhxProp))           },        
        { "door",           new GameBaseClass(typeof(PhxDoor.ClassProperties),           typeof(PhxDoor))           },
        { "animatedprop",   new GameBaseClass(typeof(PhxAnimatedProp.ClassProperties),   typeof(PhxAnimatedProp))   },

        { "destructablebuilding", new GameBaseClass(typeof(PhxDestructableBuilding.ClassProperties), typeof(PhxDestructableBuilding))   },
        { "armedbuilding",  new GameBaseClass(typeof(PhxArmedBuilding.ClassProperties),  typeof(PhxArmedBuilding))  },

        // Plain and animated buildings. Both were unregistered, so every
        // indestructible structure on every map imported as geometry with no
        // instance behind it: no odf collision masks applied, no attached odfs
        // or effects built, and nothing addressable by name from Lua. A
        // building is a prop that does not move and an animatedbuilding is an
        // animated prop, so they map onto exactly those implementations
        // rather than duplicating them. (Destructible structures are a
        // different class and keep their own implementation above.)
        { "building",         new GameBaseClass(typeof(PhxProp.ClassProperties),         typeof(PhxProp))         },
        { "animatedbuilding", new GameBaseClass(typeof(PhxAnimatedProp.ClassProperties), typeof(PhxAnimatedProp)) },

        // Mission objects. Unregistered until now, which meant a map could
        // parse them perfectly and still have no minefields, no beacons and no
        // usable terminals - and objectives written around them could never
        // complete.
        { "mine",           new GameBaseClass(typeof(PhxMine.ClassProperties),           typeof(PhxMine))           },
        { "beacon",         new GameBaseClass(typeof(PhxBeacon.ClassProperties),         typeof(PhxBeacon))         },
        { "remoteterminal", new GameBaseClass(typeof(PhxRemoteTerminal.ClassProperties), typeof(PhxRemoteTerminal)) },

        // Remote charges. The importer enumerated `detonator` as a real base
        // class and nothing was registered for it, so PhxSoldier.Init got a
        // null back and skipped the WEAPONSECTION entry naming it - the
        // engineer's detpack did not exist at all. Its ordnance is base 'mine'
        // (measured), which is why PhxMine needed no new type to carry it.
        { "detonator",      new GameBaseClass(typeof(PhxGenericWeapon.ClassProperties), typeof(PhxDetonator))  },

        // The engineer's fusioncutter. Also unregistered, so the WEAPONSECTION
        // entry naming it was skipped and an engineer carried nothing in that
        // slot. NOT the dispensers - `dispenser` is a separate base class with
        // more than twice as many leaves, and is a separate job.
        { "repair",         new GameBaseClass(typeof(PhxRepairWeapon.ClassProperties), typeof(PhxRepairWeapon)) },


        // This probably doesn't need to be an instance, but skin changer mods might modify leafpatch classes
        // e.g. season changer on Marth's Pioneer Trails...
        { "leafpatch",      new GameBaseClass(typeof(PhxLeafPatchClass),                 typeof(PhxLeafPatch))      },

        // Ground cover, and unregistered until now - so it was skipped at
        // import with no error, an unregistered base class being something the
        // importer declines to build rather than something it fails at.
        // Probing the shipped maps puts that at 66 instances: nab_prop_flowers
        // x52 on Naboo, yav_prop_grass_tall x10 and yav_prop_grass x4 on Yavin.
        { "grasspatch",     new GameBaseClass(typeof(PhxGrassPatchClass),                typeof(PhxGrassPatch))     },

        // Light shafts through a canopy - 135 instances, 134 of them on Endor.
        // The only one of these gaps that was genuinely invisible rather than
        // merely inert: a godray odf has no GeometryName, so unlike a flag or a
        // trap there was no model for the class loader to build in the absence
        // of a runtime type.
        { "godray",         new GameBaseClass(typeof(PhxGodRayClass),                    typeof(PhxGodRay))         },

        // The world object under PhxFlag's rules. Without it no PhxInstance was
        // created for a flag, so PhxLuaAPI.ResolveFlag found nothing to attach
        // to and every AddFlag ended at its own warning - CTF, 1-flag and Hunt
        // had no flag on any of the 24 maps that place one.
        { "flag",           new GameBaseClass(typeof(PhxFlagItem.ClassProperties),       typeof(PhxFlagItem))       },

        // Endor's Ewok tree smash, and nothing else in the stock game.
        // Springs and resets; deliberately does not damage - see PhxTrap.
        { "trap",           new GameBaseClass(typeof(PhxTrap.ClassProperties),           typeof(PhxTrap))           },

        // Positional ambience: machinery, shield hums, water, tractor beams.
        // Unregistered until now, so every map imported its soundemitters
        // world as a few hundred silent empties.
        { "soundambiencestatic", new GameBaseClass(typeof(PhxSoundAmbienceStatic.ClassProperties), typeof(PhxSoundAmbienceStatic)) },

        // One instance in the stock game, on Yavin. Shares the static emitter's
        // properties and its behaviour: what the two base classes distinguish
        // is a memory-budget decision about long clips that does not survive
        // into Unity, where load type belongs to the imported asset rather than
        // to the emitter placing it.
        { "soundambiencestreaming", new GameBaseClass(typeof(PhxSoundAmbienceStatic.ClassProperties), typeof(PhxSoundAmbienceStreaming)) },

        // Drifting dust and mist volumes, and the periodic ambient rumble that
        // goes with them. Both were unregistered.
        { "dusteffect",     new GameBaseClass(typeof(PhxDustEffect.ClassProperties),     typeof(PhxDustEffect))     },
        { "rumbleeffect",   new GameBaseClass(typeof(PhxRumbleEffect.ClassProperties),   typeof(PhxRumbleEffect))   },

        { "commandpost",    new GameBaseClass(typeof(PhxCommandpost.ClassProperties),    typeof(PhxCommandpost))    },
        { "hologram",       new GameBaseClass(typeof(PhxHoloIcon.ClassProperties),       typeof(PhxHoloIcon))       },
        { "soldier",        new GameBaseClass(typeof(PhxSoldier.ClassProperties),        typeof(PhxSoldier))        },
        { "powerupstation", new GameBaseClass(typeof(PhxPowerupstation.ClassProperties), typeof(PhxPowerupstation)) },
        
        { "hover",          new GameBaseClass(typeof(PhxHover.ClassProperties),          typeof(PhxHover))          },
        { "commandhover",   new GameBaseClass(typeof(PhxHover.ClassProperties),          typeof(PhxHover))          },

        { "flyer",          new GameBaseClass(typeof(PhxFlyer.ClassProperties),          typeof(PhxFlyer))          },
        { "commandflyer",   new GameBaseClass(typeof(PhxFlyer.ClassProperties),          typeof(PhxFlyer))          },

        // AT-ST / AT-AT / AT-TE / spider droid / hailfire. Previously
        // unregistered - none of them spawned.
        { "walker",         new GameBaseClass(typeof(PhxWalker.ClassProperties),         typeof(PhxWalker))         },
        { "commandwalker",  new GameBaseClass(typeof(PhxWalker.ClassProperties),         typeof(PhxWalker))         },
        
        { "vehiclespawn",   new GameBaseClass(null,                                      typeof(PhxVehicleSpawn))   },

        // Emplaced guns (map-placed turrets). Previously unregistered, so
        // none of them spawned on any map.
        { "turret",         new GameBaseClass(typeof(PhxTurret.ClassProperties),         typeof(PhxTurret))         },

        
        { "weapon",         new GameBaseClass(typeof(PhxGenericWeapon.ClassProperties),  typeof(PhxGenericWeapon))  },
        { "grenade",        new GameBaseClass(typeof(PhxGrenade.ClassProperties),        typeof(PhxGrenade))        },
        { "launcher",       new GameBaseClass(typeof(PhxGenericWeapon.ClassProperties),  typeof(PhxGenericWeapon))  },
        { "cannon",         new GameBaseClass(typeof(PhxCannon.ClassProperties),         typeof(PhxCannon))         },
        { "melee",          new GameBaseClass(typeof(PhxMeleeWeapon.ClassProperties),    typeof(PhxMeleeWeapon))    },

        
        // Right now, there's custom object pooling just for projectiles, meaning, PhxBolt's are not instantiated via
        // PhxScene.CreateInstance(), but with PhxProjectiles.FireProjectile()
        // Maybe this will be obsolete once we've got a generic object pooling for everything. Idk yet.
        { "missile",        new GameBaseClass(typeof(PhxMissileClass),                   null)                      },
        { "sticky",         new GameBaseClass(typeof(PhxStickyClass),                    null)                      },
        { "shell",          new GameBaseClass(typeof(PhxShellClass),                     null)                      },
        { "beam",           new GameBaseClass(typeof(PhxBeamClass),                      null)                      },
        { "bolt",           new GameBaseClass(typeof(PhxBoltClass),                      null)                      },
        { "bullet",         new GameBaseClass(typeof(PhxBoltClass),                      null)                      },

        { "explosion",      new GameBaseClass(typeof(PhxExplosionClass),                 null)                      },
    };

    // Both lookups tolerate a null name. A class whose inheritance chain never
    // resolved has no base class name to offer, and Dictionary.TryGetValue
    // throws on a null key rather than missing - so the caller least able to
    // do anything about it got an exception instead of "no such class".
    public static Type GetPhxInstanceType(string name)
    {
        if (!string.IsNullOrEmpty(name) && TypeDB.TryGetValue(name, out GameBaseClass cl))
        {
            return cl.InstanceType;
        }
        return null;
    }

    public static Type GetPhxClassType(string name)
    {
        if (!string.IsNullOrEmpty(name) && TypeDB.TryGetValue(name, out GameBaseClass cl))
        {
            return cl.ClassType;
        }
        return null;
    }

    /// <summary>
    /// Base class names with a runtime implementation. Used by the import
    /// validation pass to tell "this odf class has no behaviour" apart from
    /// "this odf class failed to load", which look identical in a scene.
    /// </summary>
    public static IEnumerable<string> RegisteredBaseClasses => TypeDB.Keys;

    public static bool IsRegistered(string baseClassName)
    {
        return !string.IsNullOrEmpty(baseClassName) && TypeDB.ContainsKey(baseClassName);
    }
}
