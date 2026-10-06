using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Pilot renderer for repeated Synty street pieces.
///
/// It finds renderers under objects whose names start with:
///   SM_Env_Road
///   SM_Env_Sidewalk
///
/// It groups them by spatial cell + Mesh + Material + submesh + render state,
/// disables only the original MeshRenderers, and redraws them with
/// Graphics.RenderMeshInstanced.
///
/// Colliders and GameObjects are left untouched.
/// This is a performance pilot, not the final world/HLOD system.
/// </summary>
[DisallowMultipleComponent]
public sealed class StreetInstanceRenderer : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Root containing the Demo/world geometry. If empty, this Transform is used.")]
    [SerializeField] private Transform sourceRoot;

    [Header("Grouping")]
    [Tooltip("Keeps instance batches spatially local so Unity can frustum-cull them in useful chunks.")]
    [Min(4f)]
    [SerializeField] private float cellSize = 32f;

    [Tooltip("Unity 6 commonly supports up to 511 Matrix4x4 instances per call with the default matrix layout. 500 leaves a little safety margin.")]
    [Range(1, 500)]
    [SerializeField] private int maxInstancesPerDraw = 500;

    [Header("Safety")]
    [Tooltip("Skip renderers that use baked lightmaps. Manual instancing needs a separate lightmap-data path to preserve unique lightmap scale/offset correctly.")]
    [SerializeField] private bool skipLightmappedRenderers = true;

    [Tooltip("Skip renderers using a MaterialPropertyBlock because this pilot does not yet carry per-renderer property data into the instance draw.")]
    [SerializeField] private bool skipPropertyBlockRenderers = true;

    [Header("Optional")]
    [Tooltip("Leave null to render for all cameras, matching normal world geometry. Assign a camera for a camera-specific benchmark.")]
    [SerializeField] private Camera targetCamera;

    private readonly List<Batch> batches = new List<Batch>();
    private readonly List<MeshRenderer> disabledSourceRenderers = new List<MeshRenderer>();
    private readonly Dictionary<Material, Material> runtimeMaterials = new Dictionary<Material, Material>();

    private int convertedRendererCount;
    private int skippedStaticBatchCount;
    private int skippedLightmapCount;
    private int skippedPropertyBlockCount;

    private sealed class Batch
    {
        public Mesh mesh;
        public Material material;
        public int subMeshIndex;
        public int layer;
        public uint renderingLayerMask;
        public ShadowCastingMode shadowCastingMode;
        public bool receiveShadows;
        public Matrix4x4[] matrices;
    }

    private readonly struct GroupKey : IEquatable<GroupKey>
    {
        public readonly Mesh Mesh;
        public readonly Material Material;
        public readonly int SubMeshIndex;
        public readonly int Layer;
        public readonly uint RenderingLayerMask;
        public readonly ShadowCastingMode ShadowCastingMode;
        public readonly bool ReceiveShadows;
        public readonly int CellX;
        public readonly int CellZ;

        public GroupKey(
            Mesh mesh,
            Material material,
            int subMeshIndex,
            int layer,
            uint renderingLayerMask,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            int cellX,
            int cellZ)
        {
            Mesh = mesh;
            Material = material;
            SubMeshIndex = subMeshIndex;
            Layer = layer;
            RenderingLayerMask = renderingLayerMask;
            ShadowCastingMode = shadowCastingMode;
            ReceiveShadows = receiveShadows;
            CellX = cellX;
            CellZ = cellZ;
        }

        public bool Equals(GroupKey other)
        {
            return ReferenceEquals(Mesh, other.Mesh)
                   && ReferenceEquals(Material, other.Material)
                   && SubMeshIndex == other.SubMeshIndex
                   && Layer == other.Layer
                   && RenderingLayerMask == other.RenderingLayerMask
                   && ShadowCastingMode == other.ShadowCastingMode
                   && ReceiveShadows == other.ReceiveShadows
                   && CellX == other.CellX
                   && CellZ == other.CellZ;
        }

        public override bool Equals(object obj)
        {
            return obj is GroupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Mesh != null ? Mesh.GetInstanceID() : 0;
                hash = (hash * 397) ^ (Material != null ? Material.GetInstanceID() : 0);
                hash = (hash * 397) ^ SubMeshIndex;
                hash = (hash * 397) ^ Layer;
                hash = (hash * 397) ^ (int)RenderingLayerMask;
                hash = (hash * 397) ^ (int)ShadowCastingMode;
                hash = (hash * 397) ^ (ReceiveShadows ? 1 : 0);
                hash = (hash * 397) ^ CellX;
                hash = (hash * 397) ^ CellZ;
                return hash;
            }
        }
    }

    private void OnEnable()
    {
        if (!Application.isPlaying)
            return;

        BuildRuntimeBatches();
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            return;

        RestoreSourceRenderers();
        DestroyRuntimeMaterials();
        batches.Clear();
    }

    private void LateUpdate()
    {
        for (int i = 0; i < batches.Count; ++i)
        {
            Batch batch = batches[i];

            var renderParams = new RenderParams(batch.material)
            {
                camera = targetCamera,
                layer = batch.layer,
                renderingLayerMask = batch.renderingLayerMask,
                shadowCastingMode = batch.shadowCastingMode,
                receiveShadows = batch.receiveShadows,
                lightProbeUsage = LightProbeUsage.Off
            };

            Graphics.RenderMeshInstanced(
                renderParams,
                batch.mesh,
                batch.subMeshIndex,
                batch.matrices,
                batch.matrices.Length);
        }
    }

    private void BuildRuntimeBatches()
    {
        RestoreSourceRenderers();
        DestroyRuntimeMaterials();
        batches.Clear();

        convertedRendererCount = 0;
        skippedStaticBatchCount = 0;
        skippedLightmapCount = 0;
        skippedPropertyBlockCount = 0;

        if (!SystemInfo.supportsInstancing)
        {
            Debug.LogError("StreetInstanceRenderer: This platform/API reports that GPU instancing is not supported.", this);
            enabled = false;
            return;
        }

        Transform root = sourceRoot != null ? sourceRoot : transform;
        MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        var groups = new Dictionary<GroupKey, List<Matrix4x4>>(256);

        for (int i = 0; i < renderers.Length; ++i)
        {
            MeshRenderer renderer = renderers[i];

            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            if (!IsStreetRenderer(renderer.transform, root))
                continue;

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                continue;

            // Never consume an already static-batched renderer because its runtime mesh data
            // may no longer represent the original source mesh in the way this pilot expects.
            if (renderer.isPartOfStaticBatch)
            {
                skippedStaticBatchCount++;
                continue;
            }

            if (skipLightmappedRenderers && renderer.lightmapIndex >= 0)
            {
                skippedLightmapCount++;
                continue;
            }

            if (skipPropertyBlockRenderers && renderer.HasPropertyBlock())
            {
                skippedPropertyBlockCount++;
                continue;
            }

            Material[] materials = renderer.sharedMaterials;
            int subMeshCount = mesh.subMeshCount;

            // If the material/submesh layout is unusual, leave the original renderer alone.
            if (materials == null || materials.Length < subMeshCount)
                continue;

            bool valid = true;
            for (int sub = 0; sub < subMeshCount; ++sub)
            {
                if (materials[sub] == null)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
                continue;

            Vector3 center = renderer.bounds.center;
            int cellX = Mathf.FloorToInt(center.x / cellSize);
            int cellZ = Mathf.FloorToInt(center.z / cellSize);
            Matrix4x4 matrix = renderer.localToWorldMatrix;

            for (int sub = 0; sub < subMeshCount; ++sub)
            {
                Material sourceMaterial = materials[sub];

                var key = new GroupKey(
                    mesh,
                    sourceMaterial,
                    sub,
                    renderer.gameObject.layer,
                    renderer.renderingLayerMask,
                    renderer.shadowCastingMode,
                    renderer.receiveShadows,
                    cellX,
                    cellZ);

                if (!groups.TryGetValue(key, out List<Matrix4x4> matrices))
                {
                    matrices = new List<Matrix4x4>(64);
                    groups.Add(key, matrices);
                }

                matrices.Add(matrix);
            }

            disabledSourceRenderers.Add(renderer);
            renderer.enabled = false;
            convertedRendererCount++;
        }

        foreach (KeyValuePair<GroupKey, List<Matrix4x4>> pair in groups)
        {
            GroupKey key = pair.Key;
            List<Matrix4x4> sourceMatrices = pair.Value;
            Material instancedMaterial = GetRuntimeInstancedMaterial(key.Material);

            for (int start = 0; start < sourceMatrices.Count; start += maxInstancesPerDraw)
            {
                int count = Mathf.Min(maxInstancesPerDraw, sourceMatrices.Count - start);
                Matrix4x4[] chunk = new Matrix4x4[count];
                sourceMatrices.CopyTo(start, chunk, 0, count);

                batches.Add(new Batch
                {
                    mesh = key.Mesh,
                    material = instancedMaterial,
                    subMeshIndex = key.SubMeshIndex,
                    layer = key.Layer,
                    renderingLayerMask = key.RenderingLayerMask,
                    shadowCastingMode = key.ShadowCastingMode,
                    receiveShadows = key.ReceiveShadows,
                    matrices = chunk
                });
            }
        }

        Debug.Log(
            $"StreetInstanceRenderer READY | Converted renderers: {convertedRendererCount:N0} | " +
            $"Instance draw calls: {batches.Count:N0} | " +
            $"Skipped static-batched: {skippedStaticBatchCount:N0} | " +
            $"Skipped lightmapped: {skippedLightmapCount:N0} | " +
            $"Skipped property-block: {skippedPropertyBlockCount:N0}",
            this);
    }

    private Material GetRuntimeInstancedMaterial(Material source)
    {
        if (runtimeMaterials.TryGetValue(source, out Material existing))
            return existing;

        var clone = new Material(source)
        {
            name = source.name + " [Street Instance Pilot]",
            enableInstancing = true,
            hideFlags = HideFlags.DontSave
        };

        runtimeMaterials.Add(source, clone);
        return clone;
    }

    private void RestoreSourceRenderers()
    {
        for (int i = 0; i < disabledSourceRenderers.Count; ++i)
        {
            if (disabledSourceRenderers[i] != null)
                disabledSourceRenderers[i].enabled = true;
        }

        disabledSourceRenderers.Clear();
    }

    private void DestroyRuntimeMaterials()
    {
        foreach (KeyValuePair<Material, Material> pair in runtimeMaterials)
        {
            if (pair.Value != null)
                Destroy(pair.Value);
        }

        runtimeMaterials.Clear();
    }

    private static bool IsStreetRenderer(Transform rendererTransform, Transform root)
    {
        Transform current = rendererTransform;

        while (current != null)
        {
            string objectName = current.name;

            if (objectName.StartsWith("SM_Env_Road", StringComparison.Ordinal)
                || objectName.StartsWith("SM_Env_Sidewalk", StringComparison.Ordinal))
            {
                return true;
            }

            if (current == root)
                break;

            current = current.parent;
        }

        return false;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Run this once in Edit Mode from the component context menu.
    /// Static batching has priority over GPU instancing, so this clears ONLY the
    /// Batching Static flag on detected road/sidewalk renderer GameObjects.
    /// Other static flags (Occluder, Navigation, Reflection Probe, etc.) remain untouched.
    /// </summary>
    [ContextMenu("PREPARE Streets - Clear Batching Static")]
    private void PrepareStreetsForInstancing()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("Run PREPARE Streets - Clear Batching Static in Edit Mode, not Play Mode.", this);
            return;
        }

        Transform root = sourceRoot != null ? sourceRoot : transform;
        MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        int changed = 0;
        int matched = 0;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Prepare Street Objects For GPU Instancing");

        for (int i = 0; i < renderers.Length; ++i)
        {
            MeshRenderer renderer = renderers[i];
            if (renderer == null || !IsStreetRenderer(renderer.transform, root))
                continue;

            matched++;
            GameObject go = renderer.gameObject;
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);

            if ((flags & StaticEditorFlags.BatchingStatic) == 0)
                continue;

            Undo.RecordObject(go, "Clear Batching Static");
            flags &= ~StaticEditorFlags.BatchingStatic;
            GameObjectUtility.SetStaticEditorFlags(go, flags);
            PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            EditorUtility.SetDirty(go);
            changed++;
        }

        Undo.CollapseUndoOperations(undoGroup);

        if (gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(gameObject.scene);

        Debug.Log(
            $"StreetInstanceRenderer PREPARE complete | Street renderers found: {matched:N0} | " +
            $"Batching Static cleared: {changed:N0}. Other static flags were left unchanged.",
            this);
    }
#endif
}
