namespace FireFight.Core
{
    /// <summary>
    /// Outcome of handing an order to a firefighter, used to phrase the radio
    /// response. Keeps the responder decoupled from the command enums so the
    /// acknowledgement layer does not need to know about every order type.
    /// </summary>
    public enum CommandOutcome
    {
        Accepted,   // the firefighter took the order
        Rejected,   // understood, but could not comply (e.g. no reachable position)
        Unavailable // the firefighter is not assigned / present
    }

    /// <summary>
    /// Sink for radio-style acknowledgements ("Danny copies, charging the line").
    /// <c>NPCVoiceCommands</c> depends on this interface rather than on a concrete
    /// logger, so the feedback channel can be swapped - console now, spoken audio or
    /// on-screen subtitles later - without changing command routing.
    /// </summary>
    public interface IRadioResponder
    {
        /// <summary>
        /// Report how a firefighter handled an order.
        /// </summary>
        /// <param name="firefighterName">The unit that was addressed (e.g. "Danny").</param>
        /// <param name="acknowledgement">
        /// A short radio phrase describing the action (e.g. "charging the line").
        /// </param>
        /// <param name="outcome">Whether the order was accepted, refused, or the unit was unavailable.</param>
        void Report(string firefighterName, string acknowledgement, CommandOutcome outcome);
    }
}
