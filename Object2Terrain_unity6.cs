/*

Object2Terrain v2.3 — Unity 6 Editor tool.

Original code by Eric Haines (Eric5h5):
https://web.archive.org/web/20210802120139/https://wiki.unity3d.com/index.php?title=Object2Terrain
https://www.mediafire.com/file/3oyvdjcjix2d3iu/Object2Terrain.cs

Based on the original Object2Terrain script by Eric Haines (Eric5h5).
Unity 6 updates and additional features by LabGlitch.
Original authorship remains credited to Eric Haines (Eric5h5).
Licence: Creative Commons Attribution-ShareAlike 3.0 Unported (CC BY-SA 3.0).
https://creativecommons.org/licenses/by-sa/3.0/
https://creativecommons.org/licenses/by-sa/3.0/legalcode
Source licence identified from the archived wiki notice supplied by the maintainer.
This adapted version is shared under the same CC BY-SA 3.0 licence.
Retain attribution and the licence link, identify further changes, and share
adaptations under the same licence (or a compatible licence permitted by it).
No endorsement by the original author is implied.

Modified by LabGlitch, 25 September 2026:
Unity 6 editor workflow, child-mesh conversion, transform and edge-sampling fixes,
saved TerrainData, cancellation and cleanup, source visibility/deletion with Undo,
small-gap repair, isolated spike/pit removal, optional smoothing and 16-bit RAW export.

Updated for Unity 6 by https://labglitch.com/

Install in Assets/Editor/Object2Terrain.cs
Make sure the 3D mesh has Read/Write enabled.

1. Select your 3D mesh in the scene.
2. Open Terrain → Object to Terrain.
3. Start at 513, then click Create Terrain.
4. Choose After conversion: Keep original, Hide original, or Delete original.
Hide disables the source hierarchy. Delete removes it from the scene only.
Undo restores the source and removes the generated terrain; TerrainData is retained.
5. Optional cleanup: fill small enclosed missed-sample gaps, remove isolated
   spikes/pits, and smooth valid surfaces. Smoothing strength 0 leaves them unchanged.
6. Save heightmap as RAW exports the chosen TerrainData as unsigned 16-bit
   little-endian data, with Unity Z rows in ascending order. Import with
   Bit 16, Windows/Little Endian (also on Mac), and Flip Vertically OFF.
   Match the displayed resolution and terrain size. RAW stores heights only.

*/
   


#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public class Object2Terrain : EditorWindow
{
    private static readonly int[] Resolutions = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
    private static readonly string[] ResolutionLabels = { "33", "65", "129", "257", "513", "1025", "2049", "4097" };
    private enum SourceAction { KeepOriginal, HideOriginal, DeleteOriginal }
    private static readonly string[] SourceActionLabels = { "Keep original", "Hide original", "Delete original" };
    [SerializeField] private SourceAction afterConversion = SourceAction.HideOriginal;
    [SerializeField] private GameObject source;
    [SerializeField] private int resolution = 513;
    [SerializeField] private bool includeInactive;
    [SerializeField] private float border;
    [SerializeField] private float baseDepth = 2f;
    [SerializeField] private float headroom;
    [SerializeField] private float heightOffset;
    [SerializeField] private bool sampleBackfaces = true;
    [SerializeField] private bool fillGaps = true;
    [SerializeField] private int maxGapSamples = 4;
    [SerializeField] private float gapHeightTolerance = 2f;
    [SerializeField] private bool removeSpikes;
    [SerializeField] private float spikeThreshold = 1f;
    [SerializeField] private float smoothingStrength;
    [SerializeField] private int smoothingPasses = 1;
    [SerializeField] private float smoothingEdgeLimit = 2f;
    [SerializeField] private TerrainData exportData;
    private Vector2 scroll;

    [MenuItem("Terrain/Object to Terrain", false, 2000)]
    private static void OpenWindow()
    {
        var window = GetWindow<Object2Terrain>("Object to Terrain");
        window.minSize = new Vector2(430, 480);
        window.source = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        try { DrawWindowContents(); }
        finally { EditorGUILayout.EndScrollView(); }
    }

    private void DrawWindowContents()
    {
        EditorGUILayout.LabelField("Object to Terrain · v2.3", EditorStyles.boldLabel);
        source = (GameObject)EditorGUILayout.ObjectField("Source root", source, typeof(GameObject), true);
        if (GUILayout.Button("Use selected object")) source = Selection.activeGameObject;
        resolution = EditorGUILayout.IntPopup("Heightmap resolution", resolution, ResolutionLabels, Resolutions);
        includeInactive = EditorGUILayout.Toggle("Include inactive children", includeInactive);
        border = Mathf.Max(0, EditorGUILayout.FloatField(new GUIContent("Border (metres)", "Extra ground on each X/Z side."), border));
        baseDepth = Mathf.Max(0, EditorGUILayout.FloatField(new GUIContent("Base depth (metres)", "Distance below the lowest source vertex. Areas without a mesh hit stay at this terrain base."), baseDepth));
        headroom = Mathf.Max(0, EditorGUILayout.FloatField(new GUIContent("Extra height (metres)", "Additional vertical range above the source."), headroom));
        heightOffset = EditorGUILayout.FloatField(new GUIContent("Surface offset (metres)", "Moves sampled surfaces up/down within the terrain's range. Values outside that range are clamped."), heightOffset);
        sampleBackfaces = EditorGUILayout.Toggle(new GUIContent("Include back-facing triangles", "Useful for thin or reversed mesh surfaces."), sampleBackfaces);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Heightmap cleanup", EditorStyles.boldLabel);
        fillGaps = EditorGUILayout.Toggle("Fill small enclosed gaps", fillGaps);
        using (new EditorGUI.DisabledScope(!fillGaps))
        {
            maxGapSamples = EditorGUILayout.IntSlider(new GUIContent("Max gap samples", "Maximum connected missed samples per hole. Exterior gaps and border-connected regions are never filled."), maxGapSamples, 1, 64);
            gapHeightTolerance = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent("Gap height tolerance (m)", "Skip a gap if its surrounding heights differ by more than this amount, to protect cliffs."), gapHeightTolerance));
        }
        removeSpikes = EditorGUILayout.Toggle("Remove isolated spikes/pits", removeSpikes);
        using (new EditorGUI.DisabledScope(!removeSpikes))
            spikeThreshold = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent("Spike threshold (m)", "Replace an isolated sample only if it is this far beyond all eight neighbours and those neighbours form a nearly level surface."), spikeThreshold));
        smoothingStrength = EditorGUILayout.Slider("Smoothing strength", smoothingStrength, 0f, 1f);
        using (new EditorGUI.DisabledScope(smoothingStrength <= 0f))
        {
            smoothingPasses = EditorGUILayout.IntSlider("Smoothing passes", smoothingPasses, 1, 10);
            smoothingEdgeLimit = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent("Smooth edge limit (m)", "Exclude neighbours whose height differs by more than this amount. Keeps large cliff steps out of the average."), smoothingEdgeLimit));
        }
        EditorGUILayout.HelpBox("Cleanup is approximate. Small intentional holes or isolated peaks can also qualify. Turn each option off to preserve those features. Smoothing does not fill unsampled ground.", MessageType.None);
        EditorGUILayout.Space();
        afterConversion = (SourceAction)EditorGUILayout.Popup("After conversion", (int)afterConversion, SourceActionLabels);
        EditorGUILayout.LabelField(afterConversion == SourceAction.DeleteOriginal
            ? "Removes the source and its children from the scene. Supports Undo."
            : afterConversion == SourceAction.HideOriginal
                ? "Disables the source and its children. Supports Undo."
                : "Leaves the source unchanged.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.HelpBox("Converts static MeshFilters under the source into world-aligned terrain. Only the highest surface at each X/Z position is retained. Caves, overhangs, materials and textures are not transferred. Exclude props and lower-detail LOD copies from the source hierarchy.", MessageType.Info);
        if (resolution >= 2049)
            EditorGUILayout.HelpBox("This resolution needs millions of raycasts. Start at 513 or 1025.", MessageType.Warning);
        using (new EditorGUI.DisabledScope(source == null || EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Create Terrain", GUILayout.Height(30)))
            {
                CreateTerrain();
                GUIUtility.ExitGUI();
            }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Export heightmap", EditorStyles.boldLabel);
        exportData = (TerrainData)EditorGUILayout.ObjectField("Terrain data", exportData, typeof(TerrainData), false);
        if (GUILayout.Button("Use selected Terrain / TerrainData"))
        {
            Terrain selectedTerrain = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<Terrain>() : null;
            if (selectedTerrain != null) exportData = selectedTerrain.terrainData;
            else if (Selection.activeObject is TerrainData selectedData) exportData = selectedData;
            GUIUtility.ExitGUI();
        }
        using (new EditorGUI.DisabledScope(exportData == null || EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Save heightmap as RAW (16-bit)")) ExportRaw();
        if (exportData != null)
            EditorGUILayout.HelpBox(RawImportSettings(exportData), MessageType.None);
    }

    private void CreateTerrain()
    {
        Scene temporaryScene = default;
        var temporaryMeshes = new List<Mesh>();
        var samplingColliders = new List<MeshCollider>();
        TerrainData data = null;
        GameObject terrainObject = null;
        string assetPath = null;
        bool completed = false;
        bool previousBackfaces = Physics.queriesHitBackfaces;
        Scene previousActiveScene = SceneManager.GetActiveScene();

        try
        {
            if (source == null || EditorUtility.IsPersistent(source) ||
                !source.scene.IsValid() || !source.scene.isLoaded ||
                EditorSceneManager.IsPreviewScene(source.scene))
                throw new InvalidOperationException("Select a mesh object in a normal loaded scene, not a Project asset or an object in Prefab Mode.");
            if (Array.IndexOf(Resolutions, resolution) < 0)
                throw new InvalidOperationException("Choose a supported heightmap resolution.");
            if (!Finite(border) || !Finite(baseDepth) || !Finite(headroom) || !Finite(heightOffset) ||
                !Finite(gapHeightTolerance) || !Finite(spikeThreshold) || !Finite(smoothingStrength) || !Finite(smoothingEdgeLimit))
                throw new InvalidOperationException("All size and offset values must be finite numbers.");

            Scene destinationScene = source.scene;
            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(includeInactive);
            var usable = new List<MeshFilter>();
            foreach (MeshFilter filter in filters)
            {
                if (filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0) continue;
                if (!filter.sharedMesh.isReadable)
                    throw new InvalidOperationException("Mesh '" + filter.sharedMesh.name + "' is not readable. Enable Read/Write in its model import settings and apply the change.");
                bool hasTriangles = false;
                for (int sub = 0; sub < filter.sharedMesh.subMeshCount; sub++)
                    if (filter.sharedMesh.GetTopology(sub) == MeshTopology.Triangles && filter.sharedMesh.GetIndexCount(sub) >= 3)
                        hasTriangles = true;
                if (hasTriangles) usable.Add(filter);
            }
            if (usable.Count == 0)
                throw new InvalidOperationException("No readable triangle MeshFilters found. Place the terrain mesh under the selected source. Skinned meshes are not supported.");

            // Editor-only scene creation. Additive keeps every existing scene open.
            temporaryScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(temporaryScene);
            // Query each temporary collider directly: unrelated scene colliders cannot be hit.
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < usable.Count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Object to Terrain", "Preparing mesh " + (i + 1) + " / " + usable.Count, 0.1f * i / usable.Count))
                    throw new OperationCanceledException();
                MeshFilter filter = usable[i];
                Mesh original = filter.sharedMesh;
                Vector3[] vertices = original.vertices;
                Matrix4x4 matrix = filter.transform.localToWorldMatrix;
                for (int v = 0; v < vertices.Length; v++)
                {
                    vertices[v] = matrix.MultiplyPoint3x4(vertices[v]);
                    if (!Finite(vertices[v].x) || !Finite(vertices[v].y) || !Finite(vertices[v].z))
                        throw new InvalidOperationException("Source contains non-finite vertex coordinates.");
                    if (!hasBounds) { bounds = new Bounds(vertices[v], Vector3.zero); hasBounds = true; }
                    else bounds.Encapsulate(vertices[v]);
                }
                var triangles = new List<int>();
                for (int sub = 0; sub < original.subMeshCount; sub++)
                    if (original.GetTopology(sub) == MeshTopology.Triangles)
                        triangles.AddRange(original.GetTriangles(sub));
                // Preserve outward winding when a transform includes a reflection.
                if (matrix.determinant < 0)
                    for (int t = 0; t < triangles.Count; t += 3)
                    { int swap = triangles[t + 1]; triangles[t + 1] = triangles[t + 2]; triangles[t + 2] = swap; }
                var mesh = new Mesh { name = "Object2Terrain temporary mesh", indexFormat = IndexFormat.UInt32 };
                temporaryMeshes.Add(mesh);
                mesh.vertices = vertices;
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                var proxy = new GameObject("Object2Terrain sampling collider");
                SceneManager.MoveGameObjectToScene(proxy, temporaryScene);
                // Do not use HideFlags here: they can detach objects from their physics scene.
                var collider = proxy.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                samplingColliders.Add(collider);
            }

            if (!hasBounds || bounds.size.x <= 0.001f || bounds.size.z <= 0.001f)
                throw new InvalidOperationException("The source must have a non-zero width and length.");
            Vector3 origin = new Vector3(bounds.min.x - border, bounds.min.y - baseDepth, bounds.min.z - border);
            Vector3 size = new Vector3(bounds.size.x + border * 2, Mathf.Max(1f, bounds.size.y + baseDepth + headroom), bounds.size.z + border * 2);
            if (!Finite(size.x) || !Finite(size.y) || !Finite(size.z))
                throw new InvalidOperationException("The resulting terrain dimensions are too large.");
            Physics.queriesHitBackfaces = sampleBackfaces;
            Physics.SyncTransforms();
            var heights = new float[resolution, resolution];
            var sampled = new bool[resolution, resolution];
            float clearance = Mathf.Max(1f, bounds.size.y * 0.01f);
            float rayY = bounds.max.y + clearance;
            float rayLength = bounds.size.y + 2 * clearance;
            int hitCount = 0;
            int clampedCount = 0;
            for (int z = 0; z < resolution; z++)
            {
                if (z % 4 == 0 && EditorUtility.DisplayCancelableProgressBar("Object to Terrain",
                    "Sampling row " + (z + 1) + " / " + resolution, 0.1f + 0.85f * z / (resolution - 1)))
                    throw new OperationCanceledException();
                for (int x = 0; x < resolution; x++)
                {
                    // N samples span N-1 intervals, including both boundaries.
                    Vector3 start = new Vector3(origin.x + size.x * x / (resolution - 1), rayY,
                        origin.z + size.z * z / (resolution - 1));
                    Ray ray = new Ray(start, Vector3.down);
                    bool found = false;
                    float closestDistance = rayLength;
                    float surfaceY = 0f;
                    foreach (MeshCollider samplingCollider in samplingColliders)
                    {
                        // Cheap X/Z rejection avoids a physics query for distant child meshes.
                        Bounds meshBounds = samplingCollider.bounds;
                        if (start.x < meshBounds.min.x || start.x > meshBounds.max.x ||
                            start.z < meshBounds.min.z || start.z > meshBounds.max.z) continue;
                        if (!samplingCollider.Raycast(ray, out RaycastHit hit, closestDistance)) continue;
                        closestDistance = hit.distance;
                        surfaceY = hit.point.y;
                        found = true;
                    }
                    if (!found) continue;
                    float normalized = (surfaceY + heightOffset - origin.y) / size.y;
                    if (normalized < 0 || normalized > 1) clampedCount++;
                    heights[z, x] = Mathf.Clamp01(normalized);
                    sampled[z, x] = true;
                    hitCount++;
                }
            }
            if (hitCount == 0)
                throw new InvalidOperationException("No mesh surfaces were hit. Check the source geometry and try including back-facing triangles.");

            int repairedGaps = fillGaps ? FillSmallGaps(heights, sampled, Mathf.Clamp(maxGapSamples, 1, 64), Mathf.Max(0.01f, gapHeightTolerance) / size.y) : 0;
            int repairedSpikes = removeSpikes ? RemoveIsolatedSpikes(heights, sampled, Mathf.Max(0.01f, spikeThreshold) / size.y) : 0;
            if (smoothingStrength > 0)
                SmoothHeights(heights, sampled, Mathf.Clamp01(smoothingStrength), Mathf.Clamp(smoothingPasses, 1, 10), Mathf.Max(0.01f, smoothingEdgeLimit) / size.y);

            EditorUtility.DisplayProgressBar("Object to Terrain", "Saving TerrainData", 0.97f);
            data = new TerrainData { name = source.name + " Terrain", heightmapResolution = resolution };
            data.size = size;
            data.SetHeights(0, 0, heights);
            const string folder = "Assets/GeneratedTerrains";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "GeneratedTerrains");
            // A stable safe filename; the TerrainData's display name retains the source name.
            assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/Object2Terrain.asset");
            AssetDatabase.CreateAsset(data, assetPath);
            SceneManager.SetActiveScene(destinationScene);
            terrainObject = Terrain.CreateTerrainGameObject(data);
            SceneManager.MoveGameObjectToScene(terrainObject, destinationScene);
            terrainObject.name = source.name + " Terrain";
            terrainObject.transform.position = origin;
            AssetDatabase.SaveAssets();
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Object to Terrain");
            Undo.RegisterCreatedObjectUndo(terrainObject, "Object to Terrain");
            EditorSceneManager.MarkSceneDirty(destinationScene);
            Selection.activeGameObject = terrainObject;
            completed = true;
            exportData = data;
            // Only change the source after the terrain and its asset exist successfully.
            string sourceResult = ApplySourceAction();
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("Object2Terrain: sampled " + usable.Count + " meshes, " + hitCount + " surface hits. TerrainData saved to " + assetPath +
                ". Filled " + repairedGaps + " gap samples; removed " + repairedSpikes + " isolated spikes/pits. " + sourceResult + " Undo restores source changes and removes the scene terrain; the saved TerrainData asset is retained.", terrainObject);
            if (clampedCount > 0)
                Debug.LogWarning("Object2Terrain: " + clampedCount + " samples were clamped. Increase base depth/extra height or reduce the surface offset.", terrainObject);
        }
        catch (OperationCanceledException) { Debug.Log("Object2Terrain: conversion cancelled."); }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Object to Terrain", exception.Message, "OK");
        }
        finally
        {
            Physics.queriesHitBackfaces = previousBackfaces;
            EditorUtility.ClearProgressBar();
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(previousActiveScene);
            if (temporaryScene.IsValid() && temporaryScene.isLoaded)
                EditorSceneManager.CloseScene(temporaryScene, true);
            foreach (Mesh mesh in temporaryMeshes) if (mesh != null) DestroyImmediate(mesh);
            if (!completed)
            {
                if (terrainObject != null) DestroyImmediate(terrainObject);
                if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath) == data)
                    AssetDatabase.DeleteAsset(assetPath);
                if (data != null && !EditorUtility.IsPersistent(data)) DestroyImmediate(data);
            }
        }
    }

    private static void CleanupProgress(string message, float progress)
    {
        if (EditorUtility.DisplayCancelableProgressBar("Object to Terrain", message, progress))
            throw new OperationCanceledException();
    }

    // Only enclosed 8-connected components of missed raycasts are eligible.
    // Actual zero-height hits are valid samples, not gaps.
    private static int FillSmallGaps(float[,] heights, bool[,] sampled, int maximumSize, float heightTolerance)
    {
        int n = heights.GetLength(0);
        var visited = new bool[n, n];
        var queue = new Queue<int>();
        var members = new List<int>(maximumSize);
        int filled = 0;
        int examined = 0;
        for (int z = 0; z < n; z++)
        {
            if (z % 16 == 0) CleanupProgress("Finding small enclosed gaps", (float)z / (n - 1));
            for (int x = 0; x < n; x++)
            {
                if (sampled[z, x] || visited[z, x]) continue;
                members.Clear();
                visited[z, x] = true;
                queue.Enqueue(z * n + x);
                int count = 0;
                bool touchesEdge = false;
                double sum = 0;
                int neighbours = 0;
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    int cz = index / n, cx = index % n;
                    count++;
                    if (count <= maximumSize) members.Add(index);
                    if (cz == 0 || cx == 0 || cz == n - 1 || cx == n - 1) touchesEdge = true;
                    if ((++examined & 4095) == 0)
                        CleanupProgress("Checking gap boundaries", (float)examined / (n * n));
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int nz = cz + dz, nx = cx + dx;
                            if (nz < 0 || nx < 0 || nz >= n || nx >= n) continue;
                            if (sampled[nz, nx])
                            {
                                float h = heights[nz, nx];
                                low = Mathf.Min(low, h); high = Mathf.Max(high, h);
                                sum += h; neighbours++;
                            }
                            else if (!visited[nz, nx])
                            {
                                visited[nz, nx] = true;
                                queue.Enqueue(nz * n + nx);
                            }
                        }
                }
                if (touchesEdge || count > maximumSize || neighbours == 0 || high - low > heightTolerance) continue;
                float replacement = Mathf.Clamp01((float)(sum / neighbours));
                foreach (int index in members)
                {
                    heights[index / n, index % n] = replacement;
                    sampled[index / n, index % n] = true;
                    filled++;
                }
            }
        }
        return filled;
    }

    private static int RemoveIsolatedSpikes(float[,] heights, bool[,] sampled, float threshold)
    {
        int n = heights.GetLength(0), changed = 0;
        var result = (float[,])heights.Clone();
        var neighbours = new float[8];
        for (int z = 1; z < n - 1; z++)
        {
            if (z % 16 == 0) CleanupProgress("Removing isolated spikes and pits", (float)z / (n - 1));
            for (int x = 1; x < n - 1; x++)
            {
                if (!sampled[z, x]) continue;
                int count = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dz != 0) && sampled[z + dz, x + dx])
                            neighbours[count++] = heights[z + dz, x + dx];
                if (count != 8) continue;
                Array.Sort(neighbours);
                // A cliff edge has a wide neighbour range and is left alone.
                if (neighbours[7] - neighbours[0] > threshold * 0.5f) continue;
                float centre = heights[z, x];
                if (centre - neighbours[7] <= threshold && neighbours[0] - centre <= threshold) continue;
                result[z, x] = (neighbours[3] + neighbours[4]) * 0.5f;
                changed++;
            }
        }
        Array.Copy(result, heights, result.Length);
        return changed;
    }

    private static void SmoothHeights(float[,] heights, bool[,] sampled, float strength, int passes, float edgeLimit)
    {
        if (strength <= 0 || passes <= 0) return;
        int n = heights.GetLength(0);
        float[,] current = heights;
        float[,] next = (float[,])heights.Clone();
        for (int pass = 0; pass < passes; pass++)
        {
            Array.Copy(current, next, current.Length);
            for (int z = 1; z < n - 1; z++)
            {
                if (z % 16 == 0)
                    CleanupProgress("Smoothing pass " + (pass + 1) + " / " + passes, (pass + (float)z / (n - 1)) / passes);
                for (int x = 1; x < n - 1; x++)
                {
                    if (!sampled[z, x]) continue;
                    bool surrounded = true;
                    float centre = current[z, x], sum = centre;
                    int count = 1;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dz == 0 && dx == 0) continue;
                            if (!sampled[z + dz, x + dx]) { surrounded = false; continue; }
                            float neighbour = current[z + dz, x + dx];
                            if (Mathf.Abs(neighbour - centre) > edgeLimit) continue;
                            sum += neighbour; count++;
                        }
                    // Do not smooth the shoreline against unsampled seabed.
                    if (surrounded) next[z, x] = Mathf.Clamp01(Mathf.Lerp(centre, sum / count, strength));
                }
            }
            float[,] swap = current; current = next; next = swap;
        }
        if (!ReferenceEquals(current, heights)) Array.Copy(current, heights, current.Length);
    }

    private static string RawImportSettings(TerrainData terrainData)
    {
        Vector3 s = terrainData.size;
        return string.Format(CultureInfo.InvariantCulture,
            "Import RAW: Bit 16; Windows / Little Endian (also on Mac); Flip Vertically OFF.\nResolution: {0} x {0}\nTerrain size X / Y / Z: {1:0.######} / {2:0.######} / {3:0.######}",
            terrainData.heightmapResolution, s.x, s.y, s.z);
    }

    private void ExportRaw()
    {
        if (exportData == null) return;
        string path = EditorUtility.SaveFilePanel("Save 16-bit RAW heightmap", "", "Object2Terrain_" + exportData.heightmapResolution + "_LE.raw", "raw");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            int n = exportData.heightmapResolution;
            float[,] heights = exportData.GetHeights(0, 0, n, n);
            byte[] bytes = EncodeRaw16(heights);
            // Encoding completes before the chosen destination is opened.
            File.WriteAllBytes(path, bytes);
            EditorUtility.ClearProgressBar();
            string settings = RawImportSettings(exportData);
            Debug.Log("Object2Terrain: RAW saved to " + path + "\n" + settings + "\nRAW contains heights only; it does not store textures, terrain holes or world position.");
            if (EditorUtility.DisplayDialog("Heightmap saved", settings + "\n\nRAW contains heights only. It does not store textures, terrain holes or world position.", "OK", "Copy import settings")) return;
            EditorGUIUtility.systemCopyBuffer = settings;
        }
        catch (OperationCanceledException) { Debug.Log("Object2Terrain: RAW export cancelled before writing."); }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("RAW export failed", exception.Message, "OK");
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    private static byte[] EncodeRaw16(float[,] heights)
    {
        int rows = heights.GetLength(0), columns = heights.GetLength(1);
        byte[] bytes = new byte[checked(rows * columns * 2)];
        int cursor = 0;
        // SetHeights/GetHeights use [z,x]. First row is Unity's Z=0 edge.
        for (int z = 0; z < rows; z++)
        {
            if (z % 16 == 0) CleanupProgress("Encoding 16-bit RAW heightmap", (float)z / Mathf.Max(1, rows - 1));
            for (int x = 0; x < columns; x++)
            {
                int value = Mathf.RoundToInt(Mathf.Clamp01(heights[z, x]) * 65535f);
                bytes[cursor++] = (byte)(value & 255);
                bytes[cursor++] = (byte)(value >> 8);
            }
        }
        return bytes;
    }

    private string ApplySourceAction()
    {
        if (source == null || afterConversion == SourceAction.KeepOriginal)
            return "The source is unchanged.";
        try
        {
            if (afterConversion == SourceAction.HideOriginal)
            {
                Undo.RecordObject(source, "Hide original mesh");
                source.SetActive(false);
                if (PrefabUtility.IsPartOfPrefabInstance(source))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(source);
                Undo.FlushUndoRecordObjects();
                return "The source hierarchy has been disabled.";
            }
            // Unity does not allow removing an inherited child from a prefab instance.
            // Do not unpack or delete a larger prefab root automatically.
            if (PrefabUtility.IsPartOfPrefabInstance(source) &&
                !PrefabUtility.IsOutermostPrefabInstanceRoot(source) &&
                !PrefabUtility.IsAddedGameObjectOverride(source))
            {
                Debug.LogWarning("Object2Terrain: terrain created, but the source is an inherited prefab child and cannot be deleted independently. Use Hide original, or unpack the prefab yourself before deleting it.", source);
                return "The prefab child source was kept because it cannot be deleted independently.";
            }
            Undo.DestroyObjectImmediate(source);
            return "The source hierarchy has been deleted from the scene; the mesh asset is retained.";
        }
        catch (Exception exception)
        {
            // Retain the successful conversion even if a source action is rejected.
            Debug.LogWarning("Object2Terrain: terrain created, but the source action could not finish: " + exception.Message);
            return "The source action could not finish; see the Console.";
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
#endif
