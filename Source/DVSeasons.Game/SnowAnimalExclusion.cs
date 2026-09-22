using System;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class SnowAnimalExclusion
    {
        private static readonly string[] names =
        {
            "animal", "fauna", "deer", "moose", "elk", "horse", "cow", "sheep",
            "goat", "chicken", "boar", "pig", "rabbit", "fox", "wolf",
            "cat", "kitten", "dog", "puppy", "cattle", "donkey", "pony", "hare",
            "bird", "crow", "raven", "pigeon", "duck", "goose", "geese", "swan", "seagull",
            "squirrel", "badger", "hedgehog"
        };
        private static readonly string[] nonAnimalCompounds =
        { "catwalk", "catenary", "dogbox", "doghouse", "dogbone", "cowcatcher", "cattleguard", "horsepower", "gooseneck" };

        public static bool IsAnimal(Renderer renderer)
        {
            if (renderer == null) return false;
            // The shipped farm animals have Animator/SkinnedMeshRenderer, but
            // no animal MonoBehaviour or dedicated layer. Match their prefab
            // roots (Cow_rigged, PigPink_rigged, ChickenWhite_rigged, etc.).
            // Never use CompareTag: the game need not define an Animal tag.
            for (var node = renderer.transform; node != null; node = node.parent)
                if (string.Equals(node.tag, "Animal", StringComparison.Ordinal) || IsAnimalName(node.name))
                    return true;
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh != null && IsAnimalName(skinned.sharedMesh.name);
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null && IsAnimalName(filter.sharedMesh.name);
        }

        internal static bool IsAnimalName(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            // Stockcar_Cattle/Sheep are whole wagon roots. Their Cargo_* meshes
            // contain the actual livestock, and must not exempt the wagon roof.
            if ((value.StartsWith("Stockcar_", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("C_Stockcar_", StringComparison.OrdinalIgnoreCase)) &&
                value.IndexOf("_Cargo_", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var name in names)
            {
                var offset = 0;
                while (offset < value.Length)
                {
                    var index = value.IndexOf(name, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    var end = index + name.Length;
                    // The native livestock cargo meshes use Goats/Pigs; mod
                    // prefabs also commonly group Cats/Dogs under plural roots.
                    if (end < value.Length && (value[end] == 's' || value[end] == 'S') &&
                        (end + 1 == value.Length || !char.IsLetter(value[end + 1]))) end++;
                    bool start = index == 0 || !char.IsLetter(value[index - 1]) ||
                        (char.IsUpper(value[index]) && char.IsLower(value[index - 1]));
                    bool finish = end == value.Length || !char.IsLetter(value[end]) ||
                        (char.IsUpper(value[end]) && char.IsLower(value[end - 1]));
                    if (start && finish && !IsSceneryCompound(value, index)) return true;
                    offset = index + 1;
                }
            }
            // In particular, Boar must not match Board, billboard or keyboard.
            return false;
        }

        private static bool IsSceneryCompound(string value, int start)
        {
            foreach (var compound in nonAnimalCompounds)
            {
                int cursor = start, letter = 0;
                while (cursor < value.Length && letter < compound.Length)
                {
                    char c = value[cursor++];
                    if (c == '_' || c == '-' || c == ' ') continue;
                    if (char.ToLowerInvariant(c) != compound[letter]) break;
                    letter++;
                }
                if (letter == compound.Length) return true;
            }
            return false;
        }
    }
}
