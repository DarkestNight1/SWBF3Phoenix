/// <summary>
/// What a surface physically is, independent of what shader draws it.
/// </summary>
/// <remarks>
/// Everything that touches the world - a blaster bolt, a boot, an explosion,
/// rain, a landing gunship - needs to know what it just touched in order to
/// respond correctly. Before this, each of those systems answered the question
/// for itself, or did not ask it at all: impacts played one effect everywhere,
/// footsteps were silent, and rain wet a lava field the same as a hangar deck.
///
/// The stock data has no surface field to read. It does have material flags,
/// texture names and terrain layer names that are highly indicative, so
/// <see cref="BFSurfaceQuery"/> derives the type from those - downstream of
/// libswbf2, without altering the source representation.
/// </remarks>
public enum BFSurfaceType
{
    /// <summary>Nothing identified it; callers use a neutral response.</summary>
    Unknown = 0,
    Snow,
    Ice,
    Sand,
    Mud,
    Grass,
    Rock,
    Metal,
    Wood,
    Concrete,
    Water,
    Glass,
    Lava,

    /// <summary>A person or creature. Impacts on flesh are not impacts on ground.</summary>
    Flesh,
}
