using UnityEngine;
using FireFight.Core;

namespace FireFight
{
    /// <summary>
    /// Default <see cref="IRadioResponder"/> that writes radio chatter to the Unity
    /// console. Phrases acknowledgements in fireground style:
    ///   accepted    -> "Danny copies, charging the line."
    ///   rejected    -> "Danny unable: cannot reach a safe spraying position."
    ///   unavailable -> "Danny is not on scene."
    ///
    /// Plain class (not a MonoBehaviour) so it can be constructed anywhere and unit
    /// tested. Swap in an audio/subtitle responder later by implementing the same
    /// interface - no change to the command routing in NPCVoiceCommands.
    /// </summary>
    public sealed class ConsoleRadioResponder : IRadioResponder
    {
        public void Report(string firefighterName, string acknowledgement, CommandOutcome outcome)
        {
            switch (outcome)
            {
                case CommandOutcome.Accepted:
                    Debug.Log($"{firefighterName} copies, {acknowledgement}.");
                    break;
                case CommandOutcome.Rejected:
                    Debug.Log($"{firefighterName} unable: {acknowledgement}.");
                    break;
                default: // Unavailable
                    Debug.Log($"{firefighterName} is not on scene.");
                    break;
            }
        }
    }
}
