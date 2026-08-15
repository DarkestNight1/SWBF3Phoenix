/// <summary>
/// A positional ambience the original engine streamed rather than holding in
/// memory.
/// </summary>
/// <remarks>
/// The smallest gap the class census found: one instance, com_snd_amb_streaming
/// on Yavin. Registered anyway - an unregistered base class fails silently, and
/// one silent emitter costs the same to fix as eighty.
///
/// Deliberately a subclass with no body rather than a second implementation.
/// The distinction the two base classes draw is a memory-budget decision the
/// original engine made about long clips, and it does not survive into Unity:
/// an AudioClip's load type belongs to the imported asset, not to the emitter
/// that places it. So the behaviour is identical, and inheriting it is the only
/// way to keep it identical - a copy would drift, and the copy this replaced
/// had already managed to lose the ambience mute, the volume setting, the pitch
/// lookup and the guard against a max distance inside the min.
///
/// Both properties are hash-confirmed against the shipped odf: MinDistance 1.0
/// and MaxDistance 10.0, with each placement free to override them. It shares
/// <see cref="PhxSoundAmbienceStatic.ClassProperties"/> for that reason, and is
/// registered against that same type.
/// </remarks>
public class PhxSoundAmbienceStreaming : PhxSoundAmbienceStatic
{
}
