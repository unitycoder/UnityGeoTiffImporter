// https://github.com/unitycoder/UnityGeoTiffImporter

using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityLibrary.Importers
{
    public class GeoTiffTerrainWindow : EditorWindow
    {
        [Header("Input")]
        public Texture2D heightmapTexture;

        [Header("Target Terrain (optional)")]
        public Terrain targetTerrain;

        [Header("Terrain Size (meters)")]
        public float terrainWidth = 6000f;
        public float terrainLength = 6000f;
        public float terrainHeight = 100f;

        [Header("Options")]
        public bool useMetadataHeight = true; // uses DEM min/max
        public bool useMetadataSize = true;   // uses pixelSizeX/Y

        [MenuItem("Tools/GeoTIFF/Create Terrain From Heightmap")]
        public static void ShowWindow()
        {
            var window = GetWindow<GeoTiffTerrainWindow>("GeoTIFF Terrain");
            window.minSize = new Vector2(380, 360);
        }

        private void OnGUI()
        {
            GUILayout.Label("GeoTIFF -> Terrain", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            heightmapTexture = (Texture2D)EditorGUILayout.ObjectField(
                "Heightmap Texture",
                heightmapTexture,
                typeof(Texture2D),
                false);

            targetTerrain = (Terrain)EditorGUILayout.ObjectField(
                "Target Terrain (optional)",
                targetTerrain,
                typeof(Terrain),
                true);

            EditorGUILayout.Space();

            GUILayout.Label("Terrain Size (meters)", EditorStyles.boldLabel);

            useMetadataSize = EditorGUILayout.Toggle("Use Metadata Size", useMetadataSize);

            if (useMetadataSize)
            {
                EditorGUI.BeginDisabledGroup(true);
                terrainWidth = EditorGUILayout.FloatField("Width (X)", terrainWidth);
                terrainLength = EditorGUILayout.FloatField("Length (Z)", terrainLength);
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                terrainWidth = EditorGUILayout.FloatField("Width (X)", terrainWidth);
                terrainLength = EditorGUILayout.FloatField("Length (Z)", terrainLength);
            }

            useMetadataHeight = EditorGUILayout.Toggle("Use Metadata Height", useMetadataHeight);

            EditorGUI.BeginDisabledGroup(useMetadataHeight);
            terrainHeight = EditorGUILayout.FloatField("Max Height (Y)", terrainHeight);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();

            EditorGUI.BeginDisabledGroup(heightmapTexture == null);
            if (GUILayout.Button("Create / Update Terrain From Heightmap", GUILayout.Height(28)))
            {
                CreateSingleTerrain(heightmapTexture, targetTerrain, null, 0f, 0f);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            GUILayout.Label("Batch / Tiles", EditorStyles.boldLabel);

            if (GUILayout.Button("Create Terrains From Selected Heightmaps", GUILayout.Height(24)))
            {
                CreateTerrainsFromSelection();
            }

            EditorGUILayout.Space();
            GUILayout.Label("Utilities", EditorStyles.boldLabel);

            if (GUILayout.Button("Rename Selected .tif/.tiff to .geotiff", GUILayout.Height(24)))
            {
                RenameSelectedTiffsToGeotiff();
            }
        }

        private class TileInfo
        {
            public Terrain terrain;
            public float minX;
            public float maxX;
            public float minZ;
            public float maxZ;
        }

        // ---------------------------------------------------------------------
        // Single-terrain path
        // ---------------------------------------------------------------------
        private Terrain CreateSingleTerrain(Texture2D tex, Terrain existingTerrain, HeightmapMeta forcedMeta, float baseMinX, float baseMinY)
        {
            if (tex == null)
            {
                Debug.LogError("GeoTiffTerrainWindow: No heightmap texture assigned.");
                return null;
            }

            // Load metadata (min/max + pixel size + bounds) from HeightmapMeta sub-asset
            float metaMin = 0f;
            float metaMax = 0f;
            float metaPixelX = 1f;
            float metaPixelY = 1f;
            float metaMinX = 0f;
            float metaMinY = 0f;
            float metaMaxX = 0f;
            float metaMaxY = 0f;
            bool hasMeta = false;

            HeightmapMeta meta = forcedMeta;

            string texPath = AssetDatabase.GetAssetPath(tex);
            if (meta == null)
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(texPath);
                foreach (var a in assets)
                {
                    var m = a as HeightmapMeta;
                    if (m != null)
                    {
                        meta = m;
                        break;
                    }
                }
            }

            if (meta != null)
            {
                hasMeta = true;
                metaMin = meta.minHeight;
                metaMax = meta.maxHeight;
                metaPixelX = meta.pixelSizeX;
                metaPixelY = meta.pixelSizeY;
                metaMinX = meta.minX;
                metaMinY = meta.minY;
                metaMaxX = meta.maxX;
                metaMaxY = meta.maxY;
            }

            float localTerrainHeight = terrainHeight;
            float localTerrainWidth = terrainWidth;
            float localTerrainLength = terrainLength;

            if (hasMeta && useMetadataHeight)
            {
                float range = Mathf.Max(0.0001f, metaMax - metaMin);
                localTerrainHeight = range;
                Debug.Log("GeoTiffTerrainWindow: Using metadata height range " + range + " as terrainHeight for " + tex.name);
            }

            if (hasMeta && useMetadataSize)
            {
                localTerrainWidth = tex.width * metaPixelX;
                localTerrainLength = tex.height * metaPixelY;
                Debug.Log("GeoTiffTerrainWindow: Using metadata size " +
                          localTerrainWidth + " x " + localTerrainLength + " meters for " + tex.name);
            }

            if (tex.format != TextureFormat.R16)
            {
                Debug.LogWarning(
                    "GeoTiffTerrainWindow: Heightmap texture " + tex.name + " is " + tex.format +
                    ", expected R16 (16-bit). The result may be wrong.");
            }

            int srcWidth = tex.width;
            int srcHeight = tex.height;

            int srcMin = Mathf.Min(srcWidth, srcHeight);
            int terrainRes = GetClosestSupportedResolution(srcMin); // 33..4097, pow2+1

            var raw = tex.GetRawTextureData();
            int expectedBytes = srcWidth * srcHeight * 2;
            if (raw.Length < expectedBytes)
            {
                Debug.LogError(
                    "GeoTiffTerrainWindow: Raw data size (" + raw.Length +
                    ") is smaller than expected (" + expectedBytes + ") for " + tex.name);
                return null;
            }

            // Resample heightmap to terrainRes x terrainRes (nearest neighbour)
            float[,] heights = new float[terrainRes, terrainRes];

            for (int y = 0; y < terrainRes; y++)
            {
                float v = (float)y / (terrainRes - 1);
                float srcYf = v * (srcHeight - 1);
                int srcY = Mathf.RoundToInt(srcYf);

                for (int x = 0; x < terrainRes; x++)
                {
                    float u = (float)x / (terrainRes - 1);
                    float srcXf = u * (srcWidth - 1);
                    int srcX = Mathf.RoundToInt(srcXf);

                    int pixelIndex = (srcY * srcWidth + srcX) * 2;
                    if (pixelIndex + 1 >= raw.Length)
                        continue;

                    ushort v16 = BitConverter.ToUInt16(raw, pixelIndex);
                    float f = v16 / 65535f; // 0..1

                    heights[y, x] = f;
                }
            }

            var terrainData = new TerrainData();
            terrainData.heightmapResolution = terrainRes;
            terrainData.size = new Vector3(localTerrainWidth, localTerrainHeight, localTerrainLength);
            terrainData.SetHeights(0, 0, heights);

            GameObject terrainGO = null;

            if (existingTerrain != null)
            {
                existingTerrain.terrainData = terrainData;
                terrainGO = existingTerrain.gameObject;
                Debug.Log("GeoTiffTerrainWindow: Updated existing terrain '" + terrainGO.name +
                          "' with resolution " + terrainRes + ".");
            }
            else
            {
                string dir = Path.GetDirectoryName(texPath);
                string baseName = Path.GetFileNameWithoutExtension(texPath);
                string terrainAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.Combine(dir, baseName + "_TerrainData.asset"));

                AssetDatabase.CreateAsset(terrainData, terrainAssetPath);
                AssetDatabase.SaveAssets();

                terrainGO = Terrain.CreateTerrainGameObject(terrainData);
                terrainGO.name = baseName + "_Terrain";

                Debug.Log("GeoTiffTerrainWindow: Created terrain '" + terrainGO.name +
                          "' with resolution " + terrainRes + ".");
            }

            Terrain terrainComp = null;

            if (terrainGO != null)
            {
                if (hasMeta && baseMinX != float.MaxValue && baseMinY != float.MaxValue)
                {
                    float offsetX = metaMinX - baseMinX;
                    float offsetZ = metaMinY - baseMinY;
                    terrainGO.transform.position = new Vector3(offsetX, 0f, offsetZ);
                }

                terrainComp = terrainGO.GetComponent<Terrain>();
                Selection.activeObject = terrainGO;
            }

            return terrainComp;
        }

        // ---------------------------------------------------------------------
        // Batch / tiles from selection
        // ---------------------------------------------------------------------
        private void CreateTerrainsFromSelection()
        {
            var objs = Selection.objects;
            var textures = new List<Texture2D>();

            foreach (var o in objs)
            {
                var tex = o as Texture2D;
                if (tex != null)
                    textures.Add(tex);
            }

            if (textures.Count == 0)
            {
                Debug.LogWarning("GeoTiffTerrainWindow: No Texture2D assets selected in Project view.");
                return;
            }

            float baseMinX = float.MaxValue;
            float baseMinY = float.MaxValue;
            bool baseSet = false;

            var metaCache = new Dictionary<Texture2D, HeightmapMeta>();

            foreach (var tex in textures)
            {
                string texPath = AssetDatabase.GetAssetPath(tex);
                var assets = AssetDatabase.LoadAllAssetsAtPath(texPath);
                HeightmapMeta meta = null;
                foreach (var a in assets)
                {
                    meta = a as HeightmapMeta;
                    if (meta != null)
                        break;
                }

                if (meta != null)
                {
                    metaCache[tex] = meta;

                    if (!baseSet)
                    {
                        baseMinX = meta.minX;
                        baseMinY = meta.minY;
                        baseSet = true;
                    }
                }
                else
                {
                    Debug.LogWarning("GeoTiffTerrainWindow: Texture " + tex.name +
                                     " has no HeightmapMeta; it will be created at origin with no offset.");
                }
            }

            if (!baseSet)
            {
                Debug.LogWarning("GeoTiffTerrainWindow: No selected textures had metadata. Creating terrains without positional offsets.");
                baseMinX = float.MaxValue;
                baseMinY = float.MaxValue;
            }

            var tiles = new List<TileInfo>();

            foreach (var tex in textures)
            {
                HeightmapMeta meta = null;
                metaCache.TryGetValue(tex, out meta);

                Terrain t = CreateSingleTerrain(tex, null, meta, baseMinX, baseMinY);
                if (t != null)
                {
                    var data = t.terrainData;
                    var pos = t.transform.position;

                    var tile = new TileInfo
                    {
                        terrain = t,
                        minX = pos.x,
                        maxX = pos.x + data.size.x,
                        minZ = pos.z,
                        maxZ = pos.z + data.size.z
                    };

                    tiles.Add(tile);
                }
            }

            SetupTerrainNeighbors(tiles);
        }

        private void SetupTerrainNeighbors(List<TileInfo> tiles)
        {
            if (tiles == null || tiles.Count == 0)
                return;

            const float posEps = 0.1f;

            foreach (var a in tiles)
            {
                Terrain left = null;
                Terrain right = null;
                Terrain top = null;
                Terrain bottom = null;

                foreach (var b in tiles)
                {
                    if (ReferenceEquals(a, b))
                        continue;

                    // X neighbors (left/right)
                    bool zOverlap = RangesOverlap(a.minZ, a.maxZ, b.minZ, b.maxZ);
                    if (zOverlap)
                    {
                        // b is to the right of a
                        if (Mathf.Abs(b.minX - a.maxX) < posEps)
                            right = b.terrain;

                        // b is to the left of a
                        if (Mathf.Abs(a.minX - b.maxX) < posEps)
                            left = b.terrain;
                    }

                    // Z neighbors (top/bottom)
                    bool xOverlap = RangesOverlap(a.minX, a.maxX, b.minX, b.maxX);
                    if (xOverlap)
                    {
                        // b is above a (positive Z)
                        if (Mathf.Abs(b.minZ - a.maxZ) < posEps)
                            top = b.terrain;

                        // b is below a (negative Z)
                        if (Mathf.Abs(a.minZ - b.maxZ) < posEps)
                            bottom = b.terrain;
                    }
                }

                a.terrain.SetNeighbors(left, top, right, bottom);
            }

            Debug.Log("GeoTiffTerrainWindow: Terrain neighbors set for " + tiles.Count + " tiles.");
        }

        private bool RangesOverlap(float aMin, float aMax, float bMin, float bMax)
        {
            return aMin < bMax && bMin < aMax;
        }

        // ---------------------------------------------------------------------
        // Utility: rename selected .tif/.tiff to .geotiff
        // ---------------------------------------------------------------------
        private void RenameSelectedTiffsToGeotiff()
        {
            var objs = Selection.objects;
            if (objs == null || objs.Length == 0)
            {
                Debug.LogWarning("GeoTiffTerrainWindow: No assets selected to rename.");
                return;
            }

            int renamedCount = 0;

            foreach (var o in objs)
            {
                string path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path))
                    continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".tif" && ext != ".tiff")
                    continue;

                string dir = Path.GetDirectoryName(path);
                string filenameWithoutExt = Path.GetFileNameWithoutExtension(path);

                string newPath = Path.Combine(dir, filenameWithoutExt + ".geotiff");
                newPath = newPath.Replace("\\", "/");

                string error = AssetDatabase.MoveAsset(path, newPath);
                if (string.IsNullOrEmpty(error))
                {
                    renamedCount++;
                }
                else
                {
                    Debug.LogWarning("GeoTiffTerrainWindow: Failed to rename " + path + " -> " + newPath +
                                     " Error: " + error);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("GeoTiffTerrainWindow: Renamed " + renamedCount + " asset(s) to .geotiff.");
        }

        // Unity-supported heightmap resolutions (power-of-two + 1)
        private static readonly int[] SupportedResolutions =
        {
            33, 65, 129, 257, 513, 1025, 2049, 4097
        };

        private static int GetClosestSupportedResolution(int src)
        {
            int best = SupportedResolutions[0];
            int bestDiff = Mathf.Abs(src - best);

            for (int i = 1; i < SupportedResolutions.Length; i++)
            {
                int r = SupportedResolutions[i];
                int diff = Mathf.Abs(src - r);
                if (diff < bestDiff)
                {
                    best = r;
                    bestDiff = diff;
                }
            }

            return best;
        }
    }
}
