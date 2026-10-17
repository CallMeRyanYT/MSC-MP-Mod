using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MSCMP.Core
{
    public sealed class AuthoredCandidate
    {
        public readonly string AuthoredKey;
        public readonly string Archetype;
        public readonly object LocalBinding;
        public AuthoredCandidate(string authoredKey, string archetype, object localBinding)
        {
            EntityState.ValidateLabel(authoredKey, false); EntityState.ValidateLabel(archetype, false);
            if (localBinding == null) throw new ArgumentNullException("localBinding");
            AuthoredKey = authoredKey; Archetype = archetype; LocalBinding = localBinding;
        }
    }

    public static class AuthoredMapping
    {
        private sealed class BindingComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return Object.ReferenceEquals(a, b); }
            // This hash is used only to reject duplicate local bindings, never as network identity.
            public int GetHashCode(object binding) { return RuntimeHelpers.GetHashCode(binding); }
        }
        // Keys must be independently verified authored/persistent evidence. Names, order,
        // instance IDs, hierarchy paths, and current coordinates cannot establish these keys.
        public static Dictionary<ulong, object> Resolve(EntityState[] hostEntities, AuthoredCandidate[] localCandidates)
        {
            if (hostEntities == null || localCandidates == null) throw new ArgumentNullException("mapping");
            Dictionary<string, AuthoredCandidate> candidates = new Dictionary<string, AuthoredCandidate>(StringComparer.Ordinal);
            Dictionary<object, bool> bindings = new Dictionary<object, bool>(new BindingComparer());
            for (int i = 0; i < localCandidates.Length; i++)
            {
                AuthoredCandidate candidate = localCandidates[i];
                if (candidate == null || candidates.ContainsKey(candidate.AuthoredKey) || bindings.ContainsKey(candidate.LocalBinding)) throw new InvalidOperationException("Ambiguous authored entity key or duplicate local binding.");
                candidates.Add(candidate.AuthoredKey, candidate);
                bindings.Add(candidate.LocalBinding, true);
            }
            Dictionary<ulong, object> result = new Dictionary<ulong, object>();
            Dictionary<string, bool> hostKeys = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < hostEntities.Length; i++)
            {
                EntityState entity = hostEntities[i];
                if (entity == null) throw new InvalidOperationException("Missing host entity.");
                if (entity.AuthoredKey.Length == 0) continue; // Dynamic entities need a separately verified typed factory.
                if (hostKeys.ContainsKey(entity.AuthoredKey) || result.ContainsKey(entity.Id)) throw new InvalidOperationException("Ambiguous host entity mapping.");
                AuthoredCandidate candidate;
                if (!candidates.TryGetValue(entity.AuthoredKey, out candidate) || !String.Equals(candidate.Archetype, entity.Archetype, StringComparison.Ordinal)) throw new InvalidOperationException("Missing or incompatible authored entity: " + entity.AuthoredKey);
                hostKeys.Add(entity.AuthoredKey, true); result.Add(entity.Id, candidate.LocalBinding);
            }
            return result;
        }
    }
}
