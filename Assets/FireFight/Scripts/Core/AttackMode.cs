namespace FireFight.Core
{
    /// <summary>
    /// Fireground attack posture, a shared vocabulary type used by both the command
    /// parser (which recognises "offensive"/"defensive" in speech) and the NPC
    /// firefighting behaviour (which picks a standoff distance from it). Kept in a
    /// neutral Core type so neither side depends on the other.
    ///
    ///   Offensive  - aggressive close/interior attack: the firefighter moves in tight.
    ///   Defensive  - exterior attack from a safe distance: the firefighter hangs back.
    ///   Unspecified- no posture was stated; use the default standoff.
    /// </summary>
    public enum AttackMode
    {
        Unspecified,
        Offensive,
        Defensive,
    }
}
