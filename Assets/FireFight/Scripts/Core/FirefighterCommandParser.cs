using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FireFight.Core
{
    /// <summary>
    /// Turns a spoken transcript into a structured fireground command
    /// (who + what, plus optional tactical modifiers). Pure C# with no Unity
    /// dependency so it can be unit-tested and reused independently of the speech
    /// engine (Whisper) that produced the text. <c>NPCVoiceCommands</c> is the thin
    /// MonoBehaviour bridge that feeds transcripts in and acts on the result.
    ///
    /// Vocabulary is modelled on real NIMS plain-language fireground radio traffic
    /// (offensive/defensive attack, handlines, charge the line, knock down, back out,
    /// building sides Alpha/Bravo/Charlie/Delta). The game currently has three
    /// executable NPC actions - move, fight fire, stop water - plus an evacuate/back-
    /// out order, so the large body of attack/stop/move lingo all maps onto those.
    /// Tactical modifiers that the sim cannot act on YET (building side, attack mode)
    /// are still parsed and returned, so the radio phrasing flows naturally and the
    /// behaviour can be wired up later without touching the grammar.
    ///
    /// Design goals (why this replaced the old exact-phrase whitelist):
    ///   - Understand many real phrasings per order, not one fixed string.
    ///   - Tolerate the speech engine's near-misses via bounded edit-distance
    ///     matching, so "knock" still scores if Whisper hears "nock" or "knok".
    ///   - Be order-insensitive and filler/preamble tolerant: radio call-signs
    ///     ("Engine 5 to Danny, ...") and filler ("go ahead and ...") do not break it.
    ///   - Stay conservative: an explicit negation/abort or an ambiguous recipient
    ///     refuses rather than guessing wrong.
    /// </summary>
    public static class FirefighterCommandParser
    {
        public enum Recipient { Danny, Simon, Everyone }

        public enum Order
        {
            Move,          // reposition to the aimed marker (single NPC)
            FightFire,     // commit water to the marked fire
            StopSpraying,  // shut down the line / hold water
            Evacuate,      // back out / all units out - stop firefighting now
        }

        /// <summary>Building side, fireground convention: clockwise from the address side.</summary>
        public enum BuildingSide { None, Alpha, Bravo, Charlie, Delta }

        // AttackMode now lives in its own Core type (FireFight.Core.AttackMode) so the
        // NPC behaviour can share it without depending on this parser.

        /// <summary>
        /// A parsed command. <see cref="Confidence"/> is the winning order's keyword
        /// score (for logging/tuning), not a probability. <see cref="Side"/> and
        /// <see cref="Mode"/> are recognised tactical modifiers that the game does
        /// not act on yet.
        /// </summary>
        public struct Command
        {
            public Recipient Recipient;
            public Order Order;
            public BuildingSide Side;
            public AttackMode Mode;
            public float Confidence;
        }

        // ---- Vocabulary -------------------------------------------------------
        //
        // Each order owns a set of trigger phrases in real fireground language.
        // Multi-word phrases score higher (less likely to be a coincidence), so
        // "charge the line" beats a stray "line". To teach new lingo, add a phrase
        // here - no logic changes needed.

        private static readonly Dictionary<Order, string[]> OrderPhrases = new Dictionary<Order, string[]>
        {
            // COMMIT WATER TO THE FIRE (offensive or defensive - both map here; the
            // posture is captured separately as AttackMode for future use).
            [Order.FightFire] = new[]
            {
                // attack / knockdown
                "attack the fire", "attack", "knock it down", "knock down", "knock the fire down",
                "knockdown", "hit it", "hit the fire", "hit it from the exterior", "work the fire",
                "put the fire out", "take the fire",
                // handline / line work (charging a line = water on = begin attack)
                "charge the line", "charge line", "charge your line", "open the line", "open up",
                "open the nozzle", "flow water", "water on the fire", "water on", "put water on it",
                "take a handline", "pull a handline", "pull a line", "stretch a line",
                "advance the line", "advance on the fire", "lead out",
                // interior/exterior attack phrasings
                "offensive attack", "go offensive", "interior attack", "make entry", "push in",
                "defensive attack", "go defensive", "exterior attack", "surround and drown",
                // plain / legacy phrasings kept working
                "fight the fire", "fight fire", "extinguish the fire", "extinguish", "spray water",
                "shoot water", "douse it", "douse the fire", "wet it down", "put it out",
            },

            // SHUT DOWN WATER / HOLD THE LINE.
            [Order.StopSpraying] = new[]
            {
                "shut it down", "shut down the line", "shut the line", "shut down", "shut the nozzle",
                "stop the line", "stop spraying", "stop water", "stop", "knock off", "hold water",
                "hold the line", "cease water", "cease fire", "close the line", "close the nozzle",
                "close up", "bleed the line", "water off", "kill the line", "hold your water",
            },

            // REPOSITION / TAKE A POSITION (single NPC only).
            [Order.Move] = new[]
            {
                "move here", "move up", "move in", "move to this spot", "advance to here",
                "reposition", "take a position", "take position", "post up", "stage here", "stage",
                "set up here", "fall back here", "come here", "go here", "head here", "move",
                "get in position", "position here",
            },

            // BACK OUT / EVACUATE - stop all firefighting now.
            [Order.Evacuate] = new[]
            {
                "back out", "get out", "everyone out", "all out", "all units out", "evacuate",
                "evacuate the building", "abandon the building", "pull out", "pull back",
                "withdraw", "retreat", "bail out", "clear the building", "emergency evacuation",
                "exit the building", "get out now",
            },
        };

        // Recipient names and their common synonyms / likely mis-hearings.
        private static readonly Dictionary<Recipient, string[]> RecipientWords = new Dictionary<Recipient, string[]>
        {
            [Recipient.Danny] = new[] { "danny", "dan", "danning", "dani", "denny" },
            [Recipient.Simon] = new[] { "simon", "simone", "sidmon", "symon", "seiman", "simmons" },
            [Recipient.Everyone] = new[]
            {
                "everyone", "everybody", "both", "all", "team", "crew", "all units", "all hands",
            },
        };

        // Building sides (clockwise from the address side). Parsed, not yet acted on.
        private static readonly Dictionary<BuildingSide, string[]> SideWords = new Dictionary<BuildingSide, string[]>
        {
            [BuildingSide.Alpha] = new[] { "alpha", "a side", "front", "address side" },
            [BuildingSide.Bravo] = new[] { "bravo", "b side", "left side" },
            [BuildingSide.Charlie] = new[] { "charlie", "c side", "rear", "back side" },
            [BuildingSide.Delta] = new[] { "delta", "d side", "right side" },
        };

        private static readonly string[] OffensiveWords = { "offensive", "interior", "inside", "make entry", "push in" };
        private static readonly string[] DefensiveWords = { "defensive", "exterior", "outside", "surround and drown" };

        // Words that veto a command outright: negation or abort. If any appears we
        // refuse, so "don't knock it down" / "cancel that" / "disregard" never fire.
        // ("hold"/"do" are context-sensitive and handled in HasVeto.)
        private static readonly HashSet<string> VetoWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "dont", "not", "never", "cancel", "cant", "cannot", "except",
            "wait", "belay", "disregard", "negative", "abort", "standby",
        };

        // Radio preamble words we drop before parsing: call-sign routing and
        // acknowledgements that carry no order ("Engine 5 to Danny, copy, ...").
        private static readonly HashSet<string> FillerWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "to", "from", "this", "is", "copy", "copies", "received", "roger", "over",
            "be", "advised", "command", "dispatch", "please", "lets", "go", "ahead", "and",
            "okay", "ok", "now", "you", "your", "the", "a", "an", "we", "got",
            "engine", "ladder", "truck", "rescue", "squad", "battalion", "chief", "medic",
        };

        // Max single-word edit distance we forgive when matching a spoken word to a
        // keyword. 1 covers most near-misses on longer words without false matches.
        private const int MaxEditDistance = 1;

        /// <summary>
        /// Parse a transcript. Returns true and fills <paramref name="command"/> on a
        /// confident, unambiguous match; false otherwise (caller should ignore / ask
        /// the user to repeat).
        /// </summary>
        public static bool TryParse(string transcript, out Command command)
        {
            command = default;
            if (string.IsNullOrWhiteSpace(transcript)) return false;

            string[] words = Normalize(transcript);
            if (words.Length == 0) return false;

            if (HasVeto(words)) return false;

            if (!TryResolveRecipient(words, out Recipient recipient)) return false;
            if (!TryResolveOrder(words, out Order order, out float confidence)) return false;

            // "Move here" targets one firefighter at the aimed spot, never the crew.
            if (order == Order.Move && recipient == Recipient.Everyone) return false;

            command = new Command
            {
                Recipient = recipient,
                Order = order,
                Side = ResolveSide(words),
                Mode = ResolveMode(words),
                Confidence = confidence,
            };
            return true;
        }

        // ---- Normalization ----------------------------------------------------

        /// <summary>Lowercase, strip punctuation, collapse whitespace, split to words.</summary>
        private static string[] Normalize(string transcript)
        {
            string lowered = transcript.ToLowerInvariant().Replace('\u2019', '\'');
            string stripped = Regex.Replace(lowered, @"[^\w\s]", "");
            string collapsed = Regex.Replace(stripped.Trim(), @"\s+", " ");
            return collapsed.Length == 0 ? Array.Empty<string>() : collapsed.Split(' ');
        }

        private static bool HasVeto(string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];

                // "hold" is only a veto when NOT the stop phrase "hold water" /
                // "hold the line" / "hold your water" (which we accept as StopSpraying).
                if (w == "hold")
                {
                    string next = i + 1 < words.Length ? words[i + 1] : "";
                    string next2 = i + 2 < words.Length ? words[i + 2] : "";
                    bool holdStop = next == "water" || next == "line"
                                    || (next == "the" && next2 == "line")
                                    || (next == "your" && next2 == "water");
                    if (holdStop) continue;
                    return true;
                }

                // "do" only vetoes as part of "do not".
                if (w == "do")
                {
                    string next = i + 1 < words.Length ? words[i + 1] : "";
                    if (next == "not") return true;
                    continue;
                }

                if (VetoWords.Contains(w)) return true;
            }
            return false;
        }

        // ---- Recipient resolution ---------------------------------------------

        /// <summary>
        /// Exactly one recipient must be clearly present. Zero or more than one is
        /// ambiguous and refused, so we never command the wrong crew.
        /// </summary>
        private static bool TryResolveRecipient(string[] words, out Recipient recipient)
        {
            recipient = default;
            bool found = false;
            foreach (var pair in RecipientWords)
            {
                if (!ContainsPhraseOrWord(words, pair.Value)) continue;
                if (found) return false; // two different recipients named -> ambiguous
                recipient = pair.Key;
                found = true;
            }
            return found;
        }

        // ---- Order resolution -------------------------------------------------

        /// <summary>
        /// Score every order by how well the transcript matches its phrases and pick
        /// the best. Longer phrases score higher so specific lingo wins over a single
        /// stray keyword. Returns false if nothing scored.
        /// </summary>
        private static bool TryResolveOrder(string[] words, out Order order, out float bestScore)
        {
            order = default;
            bestScore = 0f;
            bool any = false;

            foreach (var pair in OrderPhrases)
            {
                float score = ScoreOrder(words, pair.Value);
                if (score > bestScore)
                {
                    bestScore = score;
                    order = pair.Key;
                    any = true;
                }
            }
            return any && bestScore > 0f;
        }

        private static float ScoreOrder(string[] words, string[] phrases)
        {
            float best = 0f;
            foreach (string phrase in phrases)
            {
                int len = CountWords(phrase);
                if (len > best && PhraseAppears(words, phrase.Split(' ')))
                    best = len;
            }
            return best;
        }

        // ---- Modifier resolution (parsed, not yet acted on) --------------------

        private static BuildingSide ResolveSide(string[] words)
        {
            foreach (var pair in SideWords)
                if (ContainsPhraseOrWord(words, pair.Value)) return pair.Key;
            return BuildingSide.None;
        }

        private static AttackMode ResolveMode(string[] words)
        {
            if (ContainsPhraseOrWord(words, OffensiveWords)) return AttackMode.Offensive;
            if (ContainsPhraseOrWord(words, DefensiveWords)) return AttackMode.Defensive;
            return AttackMode.Unspecified;
        }

        // ---- Phrase / word matching -------------------------------------------

        private static int CountWords(string phrase)
        {
            int n = 1;
            for (int i = 0; i < phrase.Length; i++) if (phrase[i] == ' ') n++;
            return n;
        }

        /// <summary>True if any candidate (single word OR multi-word phrase) appears.</summary>
        private static bool ContainsPhraseOrWord(string[] words, string[] candidates)
        {
            foreach (string c in candidates)
            {
                if (c.IndexOf(' ') >= 0)
                {
                    if (PhraseAppears(words, c.Split(' '))) return true;
                }
                else
                {
                    foreach (string w in words) if (WordsMatch(w, c)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True if every word of <paramref name="phraseWords"/> appears in
        /// <paramref name="words"/> in the same relative order (gaps allowed), each
        /// matched exactly or within the edit-distance budget. Subsequence matching
        /// lets filler words ("go ahead and knock it down") not break a phrase.
        /// </summary>
        private static bool PhraseAppears(string[] words, string[] phraseWords)
        {
            int p = 0;
            for (int i = 0; i < words.Length && p < phraseWords.Length; i++)
                if (WordsMatch(words[i], phraseWords[p])) p++;
            return p == phraseWords.Length;
        }

        /// <summary>
        /// Match a spoken word to a keyword. Exact match wins; otherwise allow up to
        /// <see cref="MaxEditDistance"/> edits, but only for words long enough that a
        /// one-edit slip is not a different short word (so "it"/"in" don't collide).
        /// </summary>
        private static bool WordsMatch(string spoken, string keyword)
        {
            if (spoken == keyword) return true;
            if (keyword.Length <= 3 || spoken.Length <= 3) return false;
            if (Math.Abs(spoken.Length - keyword.Length) > MaxEditDistance) return false;
            return LevenshteinWithin(spoken, keyword, MaxEditDistance);
        }

        /// <summary>
        /// Bounded Levenshtein: true if the edit distance between a and b is
        /// &lt;= <paramref name="max"/>. Early-outs once a whole row exceeds the budget.
        /// </summary>
        private static bool LevenshteinWithin(string a, string b, int max)
        {
            int la = a.Length, lb = b.Length;
            var prev = new int[lb + 1];
            var curr = new int[lb + 1];
            for (int j = 0; j <= lb; j++) prev[j] = j;

            for (int i = 1; i <= la; i++)
            {
                curr[0] = i;
                int rowBest = curr[0];
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = curr[j - 1] + 1;
                    int sub = prev[j - 1] + cost;
                    int v = del < ins ? del : ins;
                    if (sub < v) v = sub;
                    curr[j] = v;
                    if (v < rowBest) rowBest = v;
                }
                if (rowBest > max) return false;
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[lb] <= max;
        }
    }
}
