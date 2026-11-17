using UnityEngine;
using FireFight.Managers;
using FireFight.Core;

namespace FireFight
{
    /// <summary>Routes completed speech to one named NPC, or an explicit everyone command.</summary>
    public class NPCVoiceCommands : MonoBehaviour
    {
        [SerializeField] private NPCMovement danny;
        [SerializeField] private NPCMovement simon;
        [SerializeField] private NPCDestinationMarkers destinationMarkers;

        public enum Recipient { Danny, Simon, Everyone }
        public enum Order { Move, FightFire, StopSpraying, Evacuate }

        // Radio-acknowledgement channel. Defaults to the console responder; a test or
        // a future audio/subtitle system can supply its own via SetResponder.
        private IRadioResponder responder = new ConsoleRadioResponder();

        /// <summary>Swap the acknowledgement sink (e.g. for spoken audio or tests).</summary>
        public void SetResponder(IRadioResponder newResponder)
        {
            responder = newResponder ?? new ConsoleRadioResponder();
        }

        /// <summary>
        /// Parse a transcript into a recipient + order using the firefighter-lingo
        /// parser. Kept as a static pass-through (mapping the parser's enums to this
        /// class's enums) so existing callers and tests keep working unchanged. Note
        /// it drops tactical modifiers; <see cref="TryExecuteCommand"/> uses the full
        /// parse so it can honour the attack posture.
        /// </summary>
        public static bool TryParseCommand(string transcript, out Recipient recipient, out Order order)
        {
            recipient = default;
            order = default;
            if (!FirefighterCommandParser.TryParse(transcript, out var command)) return false;

            recipient = MapRecipient(command.Recipient);
            order = MapOrder(command.Order);
            return true;
        }

        // Explicit maps so the two enum sets stay independent (reordering one will
        // not silently mis-route commands the way a numeric cast could).
        private static Recipient MapRecipient(FirefighterCommandParser.Recipient r)
        {
            switch (r)
            {
                case FirefighterCommandParser.Recipient.Danny: return Recipient.Danny;
                case FirefighterCommandParser.Recipient.Simon: return Recipient.Simon;
                default: return Recipient.Everyone;
            }
        }

        private static Order MapOrder(FirefighterCommandParser.Order o)
        {
            switch (o)
            {
                case FirefighterCommandParser.Order.Move: return Order.Move;
                case FirefighterCommandParser.Order.FightFire: return Order.FightFire;
                case FirefighterCommandParser.Order.Evacuate: return Order.Evacuate;
                default: return Order.StopSpraying;
            }
        }

        public void ExecuteCommand(string transcript) => TryExecuteCommand(transcript);

        public bool TryExecuteCommand(string transcript)
        {
            if (!FirefighterCommandParser.TryParse(transcript, out var command))
            {
                Debug.Log("Unrecognized or ambiguous voice command: " + transcript, this);
                return false;
            }

            Recipient recipient = MapRecipient(command.Recipient);
            Order order = MapOrder(command.Order);
            AttackMode mode = command.Mode;

            if (recipient == Recipient.Danny) return Issue(danny, "Danny", order, mode);
            if (recipient == Recipient.Simon) return Issue(simon, "Simon", order, mode);

            bool dannyAccepted = Issue(danny, "Danny", order, mode);
            bool simonAccepted = simon != danny && Issue(simon, "Simon", order, mode);
            return dannyAccepted || simonAccepted;
        }

        /// <summary>
        /// Hand one order to one firefighter and voice the radio acknowledgement.
        /// Returns true if the NPC accepted the order.
        /// </summary>
        private bool Issue(NPCMovement npc, string unitName, Order order, AttackMode mode)
        {
            if (npc == null)
            {
                responder.Report(unitName, string.Empty, CommandOutcome.Unavailable);
                return false;
            }

            switch (order)
            {
                case Order.StopSpraying:
                    npc.CancelExtinguish();
                    responder.Report(unitName, "holding the line", CommandOutcome.Accepted);
                    return true;

                case Order.Evacuate:
                    // For now this stops all firefighting activity (the most meaningful
                    // response available). A future step routes the NPC to a safe point.
                    npc.CancelExtinguish();
                    responder.Report(unitName, "backing out", CommandOutcome.Accepted);
                    return true;

                case Order.FightFire:
                    return IssueFightFire(npc, unitName, mode);

                default: // Order.Move
                    return IssueMove(npc, unitName);
            }
        }

        private bool IssueFightFire(NPCMovement npc, string unitName, AttackMode mode)
        {
            if (destinationMarkers == null ||
                !destinationMarkers.TryGetFireTarget(npc, out Flammable burningObject))
            {
                responder.Report(unitName,
                    "no fire marked, place my marker on the fire", CommandOutcome.Rejected);
                return false;
            }

            bool accepted = npc.ExtinguishAt(burningObject, mode);
            responder.Report(unitName, FightFireAcknowledgement(mode),
                accepted ? CommandOutcome.Accepted : CommandOutcome.Rejected);
            return accepted;
        }

        private bool IssueMove(NPCMovement npc, string unitName)
        {
            if (destinationMarkers == null ||
                !destinationMarkers.TryGetDestination(npc, out Vector3 destination))
            {
                responder.Report(unitName, "no position marked", CommandOutcome.Rejected);
                return false;
            }

            bool accepted = npc.MoveTo(destination);
            responder.Report(unitName, "moving up",
                accepted ? CommandOutcome.Accepted : CommandOutcome.Rejected);
            return accepted;
        }

        /// <summary>Radio phrase for an attack order, flavoured by posture.</summary>
        private static string FightFireAcknowledgement(AttackMode mode)
        {
            switch (mode)
            {
                case AttackMode.Offensive: return "going offensive, interior attack";
                case AttackMode.Defensive: return "going defensive, exterior attack";
                default: return "charging the line";
            }
        }
    }
}
