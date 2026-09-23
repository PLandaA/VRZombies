using System;
using System.Collections.Generic;

namespace VRZ.Core
{
    /// Room-code rules (pure). NetworkManager uses them to create a room; SessionMenu to display it.
    public static class RoomCodeRules
    {
        /// Two-digit codes: 10..99 inclusive (90 rooms per build version).
        public const int MinCode = 10;
        public const int MaxCodeExclusive = 100;

        /// Picks a session name (prefix + code) that is not in `takenNames`. Tries `randomRange`
        /// (same contract as UnityEngine.Random.Range(int, int): max exclusive) up to `attempts`
        /// times, then falls back to scanning every code in order, so a free code is always found
        /// when one exists -- random attempts alone could miss the last free code in a busy lobby.
        /// Returns null only when all 90 codes are taken.
        public static string Pick(ICollection<string> takenNames, string prefix, Func<int, int, int> randomRange, int attempts = 100)
        {
            bool Taken(string n) => takenNames != null && takenNames.Contains(n);

            if (randomRange != null)
            {
                for (int i = 0; i < attempts; i++)
                {
                    string candidate = prefix + randomRange(MinCode, MaxCodeExclusive);
                    if (!Taken(candidate)) return candidate;
                }
            }
            for (int code = MinCode; code < MaxCodeExclusive; code++)
            {
                string candidate = prefix + code;
                if (!Taken(candidate)) return candidate;
            }
            return null;
        }

        /// The code players read and say out loud: the session name without its prefix.
        public static string DisplayCode(string sessionName, string prefix)
        {
            if (sessionName == null) return "";
            if (!string.IsNullOrEmpty(prefix) && sessionName.StartsWith(prefix, StringComparison.Ordinal))
                return sessionName.Substring(prefix.Length);
            return sessionName;
        }
    }
}
