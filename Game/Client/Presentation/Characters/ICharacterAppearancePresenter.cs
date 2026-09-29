using Game.Shared.Characters;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only adapter boundary for a concrete character system. A Synty/modular
    /// adapter can implement this later without leaking meshes/materials into Shared.
    /// </summary>
    public interface ICharacterAppearancePresenter
    {
        ushort VisualProfileId { get; }
        void ApplyAppearance(CharacterAppearanceRecipe appearance);
    }
}
