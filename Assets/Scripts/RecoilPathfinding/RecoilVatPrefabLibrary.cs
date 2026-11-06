using System;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilVatPrefabLibrary : ScriptableObject
    {
        // Global comparison override: original VAT only, regardless of scene Inspector overrides.
        public static bool LodEnabled { get; set; } = false;
        [Serializable] public struct Entry { public GameObject Source, Vat; }
        public Entry[] Entries = Array.Empty<Entry>();
        public GameObject Find(GameObject source)
        {
            foreach (var entry in Entries) if (entry.Source == source && entry.Vat != null) return entry.Vat;
            return null;
        }
    }
}
