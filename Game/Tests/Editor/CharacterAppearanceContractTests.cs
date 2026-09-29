using Game.Shared.Characters;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CharacterAppearanceContractTests
    {
        [Test]
        public void DefaultAppearance_IsValidAndEmpty()
        {
            CharacterAppearanceRecipe recipe = CharacterAppearanceRecipe.CreateDefault();
            Assert.IsTrue(recipe.IsValid(out string error), error);
            Assert.AreEqual(0, recipe.meshes.Length);
            Assert.AreEqual(0, recipe.morphs.Length);
            Assert.AreEqual(0, recipe.colors.Length);
        }

        [Test]
        public void DuplicateSemanticSlot_IsRejected()
        {
            var recipe = CharacterAppearanceRecipe.CreateDefault();
            recipe.meshes = new[]
            {
                new CharacterMeshSelection(1, 4),
                new CharacterMeshSelection(1, 7),
            };

            Assert.IsFalse(recipe.IsValid(out _));
        }

        [Test]
        public void Clone_DoesNotShareSelectionArrays()
        {
            var recipe = CharacterAppearanceRecipe.CreateDefault(2);
            recipe.meshes = new[] { new CharacterMeshSelection(1, 3) };
            CharacterAppearanceRecipe clone = recipe.Clone();
            clone.meshes[0] = new CharacterMeshSelection(1, 9);

            Assert.AreEqual(3, recipe.meshes[0].optionId);
            Assert.AreEqual(9, clone.meshes[0].optionId);
        }

        [Test]
        public void DefaultPresentationPreferences_AreValidAndNeutral()
        {
            CharacterPresentationPreferences preferences = CharacterPresentationPreferences.CreateDefault();
            Assert.IsTrue(preferences.IsValid(out string error), error);
            Assert.AreEqual(CharacterPresentationPreferences.CurrentSchemaVersion, preferences.schemaVersion);
            Assert.AreEqual(0u, preferences.revision);
            Assert.AreEqual(0, preferences.movementStyle);
        }

        [Test]
        public void PresentationPreferences_CloneIsIndependent()
        {
            CharacterPresentationPreferences preferences = CharacterPresentationPreferences.CreateDefault();
            preferences.movementStyle = 64;
            CharacterPresentationPreferences clone = preferences.Clone();
            clone.movementStyle = 220;

            Assert.AreEqual(64, preferences.movementStyle);
            Assert.AreEqual(220, clone.movementStyle);
        }
    }
}
