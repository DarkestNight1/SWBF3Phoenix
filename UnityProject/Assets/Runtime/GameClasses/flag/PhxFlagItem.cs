using UnityEngine;

/// <summary>
/// The world object a capture flag is: the pole in the ground, the banner a
/// carrier holds, and the icons that stand for both.
/// </summary>
/// <remarks>
/// Distinct from <see cref="PhxFlag"/>, which is the CTF rules - who is
/// carrying, when it returns, what scoring it triggers - and which
/// <c>PhxLuaAPI.AddFlag</c> attaches to whatever instance the mission script
/// nominates. This is the entity class underneath it, and its absence is why
/// that never worked.
///
/// The <c>flag</c> base class had no runtime type, so no <c>PhxInstance</c> was
/// ever created for a flag. <c>ResolveFlag</c> looks its target up with
/// <c>RTS.GetInstance&lt;PhxInstance&gt;</c> and then adds a PhxFlag to what it
/// finds, so with nothing to find every AddFlag call ended at its own
/// "instance that could not be resolved" warning and CTF, 1-flag and Hunt had
/// no flag on any map. The census puts that at 56 instances across 24 of the 29
/// stock maps - com_item_flag x46, com_icon_spaceflag x6, the holocrons and
/// kam_flag_embryo.
///
/// Worth being exact about what was missing, because it was not the model. A
/// flag odf carries a GeometryName and <c>LoadGeneralClass</c> builds an
/// instance's geometry whatever its base class is, so the pole was standing
/// there the whole time - inert scenery that nothing could pick up. What this
/// restores is the instance identity the scripts address it by.
///
/// Property names are hash-confirmed against the shipped odfs: GeometryName,
/// CarriedGeometryName, IconTexture, MapTexture, CarriedOffset and PickupSound
/// all resolve exactly. com_item_flag states a carried model of
/// com_icon_neutral_flag_carried and a CarriedOffset of (0, 1.5, -0.5) - over
/// the shoulder and slightly behind, which is where a carried banner sits.
/// </remarks>
public class PhxFlagItem : PhxInstance<PhxFlagItem.ClassProperties>
{
    public class ClassProperties : PhxClass
    {
        /// <summary>Model standing in the world. Built by the class loader.</summary>
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        /// <summary>Model swapped in while a soldier is carrying it.</summary>
        public PhxProp<string> CarriedGeometryName = new PhxProp<string>("");

        /// <summary>Where the carried model rides, relative to the carrier.</summary>
        public PhxProp<Vector3> CarriedOffset = new PhxProp<Vector3>(new Vector3(0f, 1.5f, -0.5f));

        public PhxProp<string> IconTexture = new PhxProp<string>("");
        public PhxProp<string> MapTexture = new PhxProp<string>("");

        public PhxProp<string> PickupSound = new PhxProp<string>("");
    }

    /// <summary>
    /// The rules component, if this flag has been registered by a script.
    /// </summary>
    /// <remarks>
    /// Not added here. A map places flags that its mode never uses - Hunt and
    /// conquest share a world file with CTF on several maps - and giving every
    /// placement live capture rules would have flags scoring in modes that have
    /// no flag scoring. PhxLuaAPI.AddFlag adds it to the ones the script names,
    /// which is the same gate that decided this before.
    /// </remarks>
    public PhxFlag Rules => GetComponent<PhxFlag>();

    public override void Init()
    {
        // Geometry is already built: LoadGeneralClass reads GeometryName and
        // attaches the model before any runtime class sees the object. There is
        // nothing to construct here, which is the whole point - this type
        // exists so the instance is addressable, not to draw anything.
    }

    public override void Destroy() { }
}
