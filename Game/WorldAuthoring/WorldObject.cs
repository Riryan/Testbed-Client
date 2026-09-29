using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>Lightweight client identity for a world object that also exists in the server map.</summary>
    [AddComponentMenu("MMO/World Object")]
    public sealed class WorldObject : MonoBehaviour
    {
        [SerializeField, HideInInspector] private long stableId;
        [SerializeField] private string definitionId = string.Empty;

        public long StableId => stableId;
        public string DefinitionId => definitionId ?? string.Empty;

        public void SetBakedIdentity(long id, string definition)
        {
            stableId = id;
            definitionId = definition ?? string.Empty;
        }
    }
}
