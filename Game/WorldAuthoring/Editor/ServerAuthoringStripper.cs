using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Removes server-only authoring markers from the temporary build-scene copy.
    /// WorldObject/WorldInteractable remain because the client may use their stable identity
    /// and authored anchors for presentation. Source scenes are not modified.
    /// </summary>
    public sealed class ServerAuthoringStripper : IProcessSceneWithReport
    {
        public int callbackOrder => 100;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            Strip<ServerMap>(scene);
            Strip<ServerSurface>(scene);
            Strip<ServerSpawn>(scene);
            Strip<PopulationRouteMarker>(scene);
            Strip<PopulationRoutePath>(scene);
            Strip<PopulationPortal>(scene);
            Strip<ServerBakeIgnore>(scene);
        }

        private static void Strip<T>(Scene scene) where T : Component
        {
            T[] components = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = components.Length - 1; i >= 0; --i)
            {
                T component = components[i];
                if (component != null && component.gameObject.scene == scene)
                    Object.DestroyImmediate(component);
            }
        }
    }
}
