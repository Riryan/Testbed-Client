using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.SceneManagement;
#endif

/// <summary>
/// Unity 6 GPU-driven world setup without a compile-time URP assembly reference.
///
/// This intentionally avoids `using UnityEngine.Rendering.Universal;` so it can live
/// inside project assemblies that do not reference the URP runtime assembly.
///
/// No component needs to be added to a scene.
///
/// Edit Mode menu:
/// MMO Tools > World > GPU Driven > Apply Maximum GPU Resident Setup
/// MMO Tools > World > GPU Driven > Audit Loaded World
/// </summary>
public sealed class WorldStaticInstanceRenderer : MonoBehaviour
{
    private const float AggressiveSmallMeshScreenPercentage = 0.10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ReportNativeGpuDrivenState()
    {
        RenderPipelineAsset pipeline = GetActivePipelineAsset();
        if (pipeline == null)
        {
            Debug.LogWarning("World GPU Driven: No Scriptable Render Pipeline asset is active.");
            return;
        }

        if (!IsUrpAsset(pipeline))
        {
            Debug.LogWarning(
                $"World GPU Driven: Active pipeline '{pipeline.GetType().FullName}' is not a UniversalRenderPipelineAsset.");
            return;
        }

        string grd = ReadMemberAsString(pipeline, "gpuResidentDrawerMode", "unknown");
        string gpuOcclusion = ReadMemberAsString(pipeline, "gpuResidentDrawerEnableOcclusionCullingInCameras", "unknown");
        string smallMesh = ReadMemberAsString(pipeline, "smallMeshScreenPercentage", "unknown");
        string srpBatcher = ReadMemberAsString(pipeline, "useSRPBatcher", "unknown");

        Debug.Log(
            $"World GPU Driven ACTIVE | GPU Resident Drawer: {grd} | " +
            $"GPU Occlusion: {gpuOcclusion} | Small Mesh Cull: {smallMesh} | " +
            $"SRP Batcher: {srpBatcher} | Compute Shaders: {SystemInfo.supportsComputeShaders}. " +
            "No managed per-frame world draw loop is running.");
    }

    private static RenderPipelineAsset GetActivePipelineAsset()
    {
        if (QualitySettings.renderPipeline != null)
            return QualitySettings.renderPipeline;

        return GraphicsSettings.defaultRenderPipeline;
    }

    private static bool IsUrpAsset(UnityEngine.Object asset)
    {
        if (asset == null)
            return false;

        Type type = asset.GetType();
        return type.Name == "UniversalRenderPipelineAsset" ||
               type.FullName == "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset";
    }

    private static string ReadMemberAsString(object target, string memberName, string fallback)
    {
        if (target == null)
            return fallback;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();

        PropertyInfo property = type.GetProperty(memberName, flags);
        if (property != null && property.CanRead)
        {
            try
            {
                object value = property.GetValue(target);
                return value != null ? value.ToString() : "null";
            }
            catch { }
        }

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                object value = field.GetValue(target);
                return value != null ? value.ToString() : "null";
            }
            catch { }
        }

        return fallback;
    }

#if UNITY_EDITOR
    [MenuItem("MMO Tools/World/GPU Driven/Apply Maximum GPU Resident Setup")]
    private static void ApplyMaximumGpuResidentSetup()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("World GPU Driven: Run setup in Edit Mode.");
            return;
        }

        RenderPipelineAsset pipeline = GetActivePipelineAsset();
        if (pipeline == null)
        {
            Debug.LogError("World GPU Driven: No active Render Pipeline Asset was found.");
            return;
        }

        if (!IsUrpAsset(pipeline))
        {
            Debug.LogError(
                $"World GPU Driven: Active pipeline '{pipeline.GetType().FullName}' is not URP. " +
                "GPU Resident Drawer setup was not changed.");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Apply Maximum GPU Resident World Setup");
        Undo.RecordObject(pipeline, "Configure GPU Resident Drawer");

        int changed = 0;
        int failed = 0;

        SetReflectedMember(pipeline, "useSRPBatcher", true, ref changed, ref failed);
        SetReflectedMember(pipeline, "supportsDynamicBatching", false, ref changed, ref failed);
        SetReflectedEnumMember(pipeline, "gpuResidentDrawerMode", "InstancedDrawing", ref changed, ref failed);
        SetReflectedMember(pipeline, "gpuResidentDrawerEnableOcclusionCullingInCameras", true, ref changed, ref failed);
        SetReflectedMember(pipeline, "smallMeshScreenPercentage", AggressiveSmallMeshScreenPercentage, ref changed, ref failed);

        EditorUtility.SetDirty(pipeline);

        int rendererDataCount = 0;
        int forwardPlusCount = 0;
        int incompatibleRendererDataCount = 0;

        SerializedObject pipelineSerialized = new SerializedObject(pipeline);
        SerializedProperty rendererDataList = pipelineSerialized.FindProperty("m_RendererDataList");

        if (rendererDataList != null && rendererDataList.isArray)
        {
            rendererDataCount = rendererDataList.arraySize;

            for (int i = 0; i < rendererDataList.arraySize; ++i)
            {
                UnityEngine.Object rendererData = rendererDataList.GetArrayElementAtIndex(i).objectReferenceValue;
                if (rendererData == null)
                    continue;

                if (rendererData.GetType().Name == "UniversalRendererData")
                {
                    Undo.RecordObject(rendererData, "Set URP Forward+");

                    int localChanged = 0;
                    int localFailed = 0;
                    SetReflectedEnumMember(rendererData, "renderingMode", "ForwardPlus", ref localChanged, ref localFailed);

                    if (localChanged > 0)
                    {
                        forwardPlusCount++;
                        EditorUtility.SetDirty(rendererData);
                        InvokeIfPresent(rendererData, "SetDirty");
                    }
                    else
                    {
                        incompatibleRendererDataCount++;
                        Debug.LogWarning(
                            $"World GPU Driven: Could not set Forward+ on renderer data '{rendererData.name}'.",
                            rendererData);
                    }
                }
                else
                {
                    incompatibleRendererDataCount++;
                    Debug.LogWarning(
                        $"World GPU Driven: Renderer Data '{rendererData.name}' is " +
                        $"{rendererData.GetType().Name}, not UniversalRendererData.",
                        rendererData);
                }
            }
        }
        else
        {
            Debug.LogWarning(
                "World GPU Driven: Could not inspect the URP Renderer List automatically. " +
                "Verify that the active Universal Renderer uses Forward+ in its Inspector.");
        }

        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        PlayerSettings.SetStaticBatchingForPlatform(target, false);
        PlayerSettings.SetDynamicBatchingForPlatform(target, false);

        AssetDatabase.SaveAssets();
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log(
            "World GPU Driven setup applied | " +
            $"Pipeline='{pipeline.name}' | Reflected settings changed={changed}, unavailable={failed} | " +
            $"Forward+ renderer data={forwardPlusCount}/{rendererDataCount} | " +
            $"Incompatible/failed renderer data={incompatibleRendererDataCount} | " +
            $"Static Batching=OFF ({target}) | Dynamic Batching=OFF.\n" +
            "IMPORTANT: In Project Settings > Graphics > Shader Stripping, set " +
            "BatchRendererGroup Variants to Keep All. Then enter Play Mode and compare the same camera position.");

        AuditLoadedWorld();
    }

    [MenuItem("MMO Tools/World/GPU Driven/Audit Loaded World")]
    private static void AuditLoadedWorld()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("World GPU Driven audit is intended for Edit Mode.");
            return;
        }

        int renderers = 0;
        int enabledRenderers = 0;
        int meshRenderersWithMesh = 0;
        int propertyBlockRenderers = 0;
        int proxyVolumeRenderers = 0;
        int staticBatchFlagged = 0;
        int repeatedRendererCount = 0;
        int repeatedGroupCount = 0;

        var meshes = new HashSet<Mesh>();
        var materials = new HashSet<Material>();
        var groups = new Dictionary<RenderGroupKey, int>(4096);

        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; ++sceneIndex)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.IsValid() || !scene.isLoaded)
                continue;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; ++rootIndex)
            {
                MeshRenderer[] sceneRenderers = roots[rootIndex].GetComponentsInChildren<MeshRenderer>(true);

                for (int r = 0; r < sceneRenderers.Length; ++r)
                {
                    MeshRenderer renderer = sceneRenderers[r];
                    if (renderer == null)
                        continue;

                    renderers++;

                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff)
                        continue;

                    enabledRenderers++;

                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null)
                        continue;

                    meshRenderersWithMesh++;
                    meshes.Add(mesh);

                    if (renderer.HasPropertyBlock())
                        propertyBlockRenderers++;

                    if (renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                        proxyVolumeRenderers++;

                    StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                    if ((flags & StaticEditorFlags.BatchingStatic) != 0)
                        staticBatchFlagged++;

                    Material[] sharedMaterials = renderer.sharedMaterials;
                    for (int m = 0; m < sharedMaterials.Length; ++m)
                    {
                        Material material = sharedMaterials[m];
                        if (material != null)
                            materials.Add(material);
                    }

                    var key = new RenderGroupKey(
                        mesh,
                        sharedMaterials,
                        renderer.gameObject.layer,
                        renderer.shadowCastingMode,
                        renderer.receiveShadows);

                    if (groups.TryGetValue(key, out int count))
                        groups[key] = count + 1;
                    else
                        groups.Add(key, 1);
                }
            }
        }

        foreach (KeyValuePair<RenderGroupKey, int> pair in groups)
        {
            if (pair.Value < 2)
                continue;

            repeatedGroupCount++;
            repeatedRendererCount += pair.Value;
        }

        RenderPipelineAsset pipeline = GetActivePipelineAsset();
        string pipelineState = pipeline == null
            ? "No active render pipeline"
            : $"Pipeline={pipeline.GetType().Name}, " +
              $"GRD={ReadMemberAsString(pipeline, "gpuResidentDrawerMode", "unknown")}, " +
              $"GPU Occlusion={ReadMemberAsString(pipeline, "gpuResidentDrawerEnableOcclusionCullingInCameras", "unknown")}, " +
              $"Small Mesh Cull={ReadMemberAsString(pipeline, "smallMeshScreenPercentage", "unknown")}, " +
              $"SRP Batcher={ReadMemberAsString(pipeline, "useSRPBatcher", "unknown")}";

        Debug.Log(
            "World GPU Driven AUDIT | " +
            $"MeshRenderers: {renderers:N0} | Enabled: {enabledRenderers:N0} | With Mesh: {meshRenderersWithMesh:N0} | " +
            $"Repeated compatible-ish renderers: {repeatedRendererCount:N0} in {repeatedGroupCount:N0} groups | " +
            $"Unique meshes: {meshes.Count:N0} | Unique materials: {materials.Count:N0} | " +
            $"MaterialPropertyBlock blockers: {propertyBlockRenderers:N0} | " +
            $"Proxy-volume light-probe blockers: {proxyVolumeRenderers:N0} | " +
            $"Batching Static flags present: {staticBatchFlagged:N0} | " + pipelineState + ".");
    }

    private static void SetReflectedMember(
        UnityEngine.Object target,
        string memberName,
        object value,
        ref int changed,
        ref int failed)
    {
        if (TrySetReflectedMember(target, memberName, value))
            changed++;
        else
        {
            failed++;
            Debug.LogWarning(
                $"World GPU Driven: Setting '{memberName}' is unavailable on {target.GetType().Name}. " +
                "This can happen between URP package versions; verify that setting manually in the URP asset.",
                target);
        }
    }

    private static void SetReflectedEnumMember(
        UnityEngine.Object target,
        string memberName,
        string enumValueName,
        ref int changed,
        ref int failed)
    {
        if (TrySetReflectedEnumMember(target, memberName, enumValueName))
            changed++;
        else
        {
            failed++;
            Debug.LogWarning(
                $"World GPU Driven: Could not set '{memberName}' to '{enumValueName}' on {target.GetType().Name}. " +
                "Verify the equivalent setting manually.",
                target);
        }
    }

    private static bool TrySetReflectedMember(object target, string memberName, object value)
    {
        if (target == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();

        PropertyInfo property = type.GetProperty(memberName, flags);
        if (property != null && property.CanWrite)
        {
            try
            {
                object converted = ConvertForTargetType(value, property.PropertyType);
                property.SetValue(target, converted);
                return true;
            }
            catch { }
        }

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                object converted = ConvertForTargetType(value, field.FieldType);
                field.SetValue(target, converted);
                return true;
            }
            catch { }
        }

        // Fallback to common serialized backing-field names.
        if (target is UnityEngine.Object unityObject)
        {
            SerializedObject so = new SerializedObject(unityObject);
            string pascal = char.ToUpperInvariant(memberName[0]) + memberName.Substring(1);
            string[] candidates = { "m_" + pascal, "m_" + memberName };

            for (int i = 0; i < candidates.Length; ++i)
            {
                SerializedProperty sp = so.FindProperty(candidates[i]);
                if (sp == null)
                    continue;

                try
                {
                    switch (sp.propertyType)
                    {
                        case SerializedPropertyType.Boolean:
                            sp.boolValue = Convert.ToBoolean(value);
                            break;
                        case SerializedPropertyType.Float:
                            sp.floatValue = Convert.ToSingle(value);
                            break;
                        case SerializedPropertyType.Integer:
                            sp.intValue = Convert.ToInt32(value);
                            break;
                        default:
                            continue;
                    }

                    so.ApplyModifiedProperties();
                    return true;
                }
                catch { }
            }
        }

        return false;
    }

    private static bool TrySetReflectedEnumMember(object target, string memberName, string enumValueName)
    {
        if (target == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();

        PropertyInfo property = type.GetProperty(memberName, flags);
        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
        {
            try
            {
                object enumValue = Enum.Parse(property.PropertyType, enumValueName, true);
                property.SetValue(target, enumValue);
                return true;
            }
            catch { }
        }

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null && field.FieldType.IsEnum)
        {
            try
            {
                object enumValue = Enum.Parse(field.FieldType, enumValueName, true);
                field.SetValue(target, enumValue);
                return true;
            }
            catch { }
        }

        return false;
    }

    private static object ConvertForTargetType(object value, Type targetType)
    {
        if (value == null)
            return null;

        Type sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType))
            return value;

        return Convert.ChangeType(value, targetType);
    }

    private static void InvokeIfPresent(object target, string methodName)
    {
        if (target == null)
            return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo method = target.GetType().GetMethod(methodName, flags, null, Type.EmptyTypes, null);
        if (method == null)
            return;

        try { method.Invoke(target, null); }
        catch { }
    }

    private readonly struct RenderGroupKey : IEquatable<RenderGroupKey>
    {
        private readonly Mesh mesh;
        private readonly int materialsHash;
        private readonly int materialCount;
        private readonly int layer;
        private readonly ShadowCastingMode shadows;
        private readonly bool receiveShadows;

        public RenderGroupKey(
            Mesh mesh,
            Material[] materials,
            int layer,
            ShadowCastingMode shadows,
            bool receiveShadows)
        {
            this.mesh = mesh;
            materialsHash = ComputeMaterialsHash(materials);
            materialCount = materials != null ? materials.Length : 0;
            this.layer = layer;
            this.shadows = shadows;
            this.receiveShadows = receiveShadows;
        }

        public bool Equals(RenderGroupKey other)
        {
            return ReferenceEquals(mesh, other.mesh)
                   && materialsHash == other.materialsHash
                   && materialCount == other.materialCount
                   && layer == other.layer
                   && shadows == other.shadows
                   && receiveShadows == other.receiveShadows;
        }

        public override bool Equals(object obj)
        {
            return obj is RenderGroupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = mesh != null ? mesh.GetInstanceID() : 0;
                hash = (hash * 397) ^ materialsHash;
                hash = (hash * 397) ^ materialCount;
                hash = (hash * 397) ^ layer;
                hash = (hash * 397) ^ (int)shadows;
                hash = (hash * 397) ^ (receiveShadows ? 1 : 0);
                return hash;
            }
        }

        private static int ComputeMaterialsHash(Material[] materials)
        {
            unchecked
            {
                int hash = 17;
                if (materials == null)
                    return hash;

                for (int i = 0; i < materials.Length; ++i)
                    hash = (hash * 31) ^ (materials[i] != null ? materials[i].GetInstanceID() : 0);

                return hash;
            }
        }
    }
#endif
}
