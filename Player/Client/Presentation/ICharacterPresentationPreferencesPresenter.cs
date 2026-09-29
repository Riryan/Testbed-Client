using Game.Shared.Characters;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only adapter boundary for character-owned movement/presentation preferences.
    /// A concrete character controller maps compact semantic state to local Animator data.
    /// </summary>
    public interface ICharacterPresentationPreferencesPresenter
    {
        void ApplyPresentationPreferences(CharacterPresentationPreferences preferences);
    }

    public interface ICharacterCreatorLocomotionPreview
    {
        void PreviewIdle();
        void PreviewWalk();
        void PreviewRun();
    }
}
