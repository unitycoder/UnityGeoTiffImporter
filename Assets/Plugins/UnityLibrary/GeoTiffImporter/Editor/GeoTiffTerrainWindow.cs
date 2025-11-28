// https://github.com/unitycoder/UnityGeoTiffImporter

using System;
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
        public float terrainWidth = 1000f;
        public float terrainLength = 1000f;
        public float terrainHeight = 200f;

        [Header("Options")]
        public bool useMetadataHeight = true; // uses DEM min/max
        public bool useMetadataSize = true;   // uses pixelSizeX/Y

        [MenuItem("Tools/GeoTIFF/Create Terrain From Heightmap")]
        public static void ShowWindow()
        {
            var window = GetWindow<GeoTiffTerrainWindow>("GeoTIFF Terrain");
            window.minSize = new Vector2(380, 280);
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

            EditorGUILayout.Space();
            EditorGUI.BeginDisabledGroup(heightmapTexture == null);
            if (GUILayout.Button("Create / Update Terrain From Heightmap", GUILayout.Height(30)))
            {
                CreateTerrain();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void CreateTerrain()
        {
            if (heightmapTexture == null)
            {
                Debug.LogError("GeoTiffTerrainWindow: No heightmap texture assigned.");
                return;
            }

            // ---------------------------------------------------------------------
            // Load metadata (min/max + pixel size) from HeightmapMeta sub-asset
            // ---------------------------------------------------------------------
            float metaMin = 0f;
            float metaMax = 0f;
            float metaPixelX = 1f;
            float metaPixelY = 1f;
            bool hasMeta = false;

            string texPath = AssetDatabase.GetAssetPath(heightmapTexture);
            var assets = AssetDatabase.LoadAllAssetsAtPath(texPath);
            foreach (var a in assets)
            {
                var m = a as HeightmapMeta;
                if (m != null)
                {
                    metaMin = m.minHeight;
                    metaMax = m.maxHeight;
                    metaPixelX = m.pixelSizeX;
                    metaPixelY = m.pixelSizeY;
                    hasMeta = true;
                    break;
                }
            }

            if (hasMeta && useMetadataHeight)
            {
                float range = Mathf.Max(0.0001f, metaMax - metaMin);
                terrainHeight = range;
                Debug.Log("GeoTiffTerrainWindow: Using metadata height range " + range + " as terrainHeight.");
            }

            if (hasMeta && useMetadataSize)
            {
                // real world width/length  pixels * pixel size (meters)
                terrainWidth = heightmapTexture.width * metaPixelX;
                terrainLength = heightmapTexture.height * metaPixelY;
                Debug.Log("GeoTiffTerrainWindow: Using metadata size " +
                          terrainWidth + " x " + terrainLength + " meters.");
            }

            if (heightmapTexture.format != TextureFormat.R16)
            {
                Debug.LogWarning(
                    "GeoTiffTerrainWindow: Heightmap texture is " + heightmapTexture.format +
                    ", expected R16 (16-bit). The result may be wrong.");
            }

            int srcWidth = heightmapTexture.width;
            int srcHeight = heightmapTexture.height;

            // ---------------------------------------------------------------------
            // Unity constraint: heightmapResolution must be power-of-two + 1
            // ---------------------------------------------------------------------
            int srcMin = Mathf.Min(srcWidth, srcHeight);
            int terrainRes = GetClosestSupportedResolution(srcMin); // 33..4097, pow2+1

            // Read raw R16 data from texture
            var raw = heightmapTexture.GetRawTextureData();
            int expectedBytes = srcWidth * srcHeight * 2;
            if (raw.Length < expectedBytes)
            {
                Debug.LogError(
                    "GeoTiffTerrainWindow: Raw data size (" + raw.Length +
                    ") is smaller than expected (" + expectedBytes + ").");
                return;
            }

            // ---------------------------------------------------------------------
            // Resample heightmap to terrainRes x terrainRes (nearest neighbour)
            // ---------------------------------------------------------------------
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

            // ---------------------------------------------------------------------
            // Create TerrainData and apply
            // ---------------------------------------------------------------------
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = terrainRes;
            terrainData.size = new Vector3(terrainWidth, terrainHeight, terrainLength);
            terrainData.SetHeights(0, 0, heights);

            GameObject terrainGO = null;

            if (targetTerrain != null)
            {
                targetTerrain.terrainData = terrainData;
                terrainGO = targetTerrain.gameObject;
                Debug.Log("GeoTiffTerrainWindow: Updated existing terrain '" + terrainGO.name +
                          "' with resolution " + terrainRes + ".");
            }
            else
            {
                string dir = System.IO.Path.GetDirectoryName(texPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(texPath);
                string terrainAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                    System.IO.Path.Combine(dir, baseName + "_TerrainData.asset"));

                AssetDatabase.CreateAsset(terrainData, terrainAssetPath);
                AssetDatabase.SaveAssets();

                terrainGO = Terrain.CreateTerrainGameObject(terrainData);
                terrainGO.name = baseName + "_Terrain";

                Debug.Log("GeoTiffTerrainWindow: Created terrain '" + terrainGO.name +
                          "' with resolution " + terrainRes + ".");
            }

            if (terrainGO != null)
            {
                Selection.activeObject = terrainGO;
            }
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
