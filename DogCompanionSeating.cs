using UnityEngine;

namespace NPCAI
{
    internal static class DogCompanionSeating
    {
        // Lower the whole seated dog instead of translating the chest bones or
        // bending the neck. Preserve its original sitting proportions.
        internal const float ForwardOffset = .45f, UpOffset = -.125f;
    }
}
