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
        public enum OutputType
        {
            Terrain,
            Mesh
        }

        public enum MeshResolution
        {
            Full,
            Medium,
            Low,
            VeryLow
        }

        [Header("Input")]
        public Texture2D heightmapTexture;

        [Header("Output Type")]
        public OutputType outputType = OutputType.Terrain;

        [Header("Target Terrain (optional)")]
        public Terrain targetTerrain;

        [Header("Mesh Options")]
        public MeshResolution meshResolution = MeshResolution.Medium;
        public bool addCollider = true;

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
            window.minSize = new Vector2(380, 460);
        }

        private void OnGUI()
        {
            GUILayout.Label("GeoTIFF -> Terrain/Mesh", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            heightmapTexture = (Texture2D)EditorGUILayout.ObjectField(
                "Heightmap Texture",
                heightmapTexture,
                typeof(Texture2D),
                false);

            EditorGUILayout.Space();
            GUILayout.Label("Output Type", EditorStyles.boldLabel);
            outputType = (OutputType)EditorGUILayout.EnumPopup("Output Type", outputType);

            EditorGUILayout.Space();

            if (outputType == OutputType.Terrain)
            {
                targetTerrain = (Terrain)EditorGUILayout.ObjectField(
                    "Target Terrain (optional)",
                    targetTerrain,
                    typeof(Terrain),
                    true);
            }
            else
            {
                GUILayout.Label("Mesh Options", EditorStyles.boldLabel);
                meshResolution = (MeshResolution)EditorGUILayout.EnumPopup("Resolution", meshResolution);
                addCollider = EditorGUILayout.Toggle("Add Mesh Collider", addCollider);
            }

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
            string buttonLabel = outputType == OutputType.Terrain 
                ? "Create / Update Terrain From Heightmap" 
                : "Create Mesh From Heightmap";
            
            if (GUILayout.Button(buttonLabel, GUILayout.Height(28)))
            {
                if (outputType == OutputType.Terrain)
                {
                    CreateSingleTerrain(heightmapTexture, targetTerrain, null, float.MaxValue, float.MaxValue);
                }
                else
                {
                    CreateSingleMesh(heightmapTexture, null, float.MaxValue, float.MaxValue);
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            GUILayout.Label("Batch / Tiles", EditorStyles.boldLabel);

            string batchButtonLabel = outputType == OutputType.Terrain
                ? "Create Terrains From Selected Heightmaps"
                : "Create Meshes From Selected Heightmaps";

            if (GUILayout.Button(batchButtonLabel, GUILayout.Height(24)))
            {
                if (outputType == OutputType.Terrain)
                {
                    CreateTerrainsFromSelection();
                }
                else
                {
                    CreateMeshesFromSelection();
                }
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
        // Single-mesh path
        // ---------------------------------------------------------------------
        private GameObject CreateSingleMesh(Texture2D tex, HeightmapMeta forcedMeta, float baseMinX, float baseMinY)
        {
            if (tex == null)
            {
                Debug.LogError("GeoTiffTerrainWindow: No heightmap texture assigned.");
                return null;
            }

            // Load metadata
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
                Debug.Log("GeoTiffTerrainWindow: Using metadata height range " + range + " as height for " + tex.name);
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

            // Calculate mesh resolution based on user choice
            int meshResX, meshResZ;
            GetMeshResolution(srcWidth, srcHeight, out meshResX, out meshResZ);

            var raw = tex.GetRawTextureData();
            int expectedBytes = srcWidth * srcHeight * 2;
            if (raw.Length < expectedBytes)
            {
                Debug.LogError(
                    "GeoTiffTerrainWindow: Raw data size (" + raw.Length +
                    ") is smaller than expected (" + expectedBytes + ") for " + tex.name);
                return null;
            }

            // Create mesh
            Mesh mesh = GenerateHeightmapMesh(raw, srcWidth, srcHeight, meshResX, meshResZ, 
                                              localTerrainWidth, localTerrainHeight, localTerrainLength);

            // Save mesh asset
            string dir = Path.GetDirectoryName(texPath);
            string baseName = Path.GetFileNameWithoutExtension(texPath);
            string meshAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(dir, baseName + "_Mesh.asset"));

            AssetDatabase.CreateAsset(mesh, meshAssetPath);
            AssetDatabase.SaveAssets();

            // Create GameObject
            GameObject meshGO = new GameObject(baseName + "_Mesh");
            var meshFilter = meshGO.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            var meshRenderer = meshGO.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = new Material(Shader.Find("Standard"));

            if (addCollider)
            {
                var meshCollider = meshGO.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = mesh;
            }

            // Position the mesh:
            // - Single file mode (baseMinX == float.MaxValue): position at origin (0,0,0)
            // - Batch/tiled mode: position relative to the base point
            if (baseMinX != float.MaxValue && baseMinY != float.MaxValue)
            {
                // Batch mode: use relative positioning
                if (hasMeta)
                {
                    float offsetX = metaMinX - baseMinX;
                    float offsetZ = metaMinY - baseMinY;
                    meshGO.transform.position = new Vector3(offsetX, 0f, offsetZ);
                    Debug.Log("GeoTiffTerrainWindow: Positioned mesh '" + meshGO.name + "' at relative position (" + offsetX + ", 0, " + offsetZ + ").");
                }
                else
                {
                    meshGO.transform.position = Vector3.zero;
                    Debug.LogWarning("GeoTiffTerrainWindow: Mesh '" + meshGO.name + "' has no metadata, positioned at origin.");
                }
            }
            else
            {
                // Single file mode: always at origin
                meshGO.transform.position = Vector3.zero;
                Debug.Log("GeoTiffTerrainWindow: Single mesh positioned at origin (0, 0, 0).");
            }

            Selection.activeObject = meshGO;

            Debug.Log("GeoTiffTerrainWindow: Created mesh '" + meshGO.name +
                      "' with resolution " + meshResX + "x" + meshResZ + ".");

            return meshGO;
        }

        private void GetMeshResolution(int srcWidth, int srcHeight, out int resX, out int resZ)
        {
            switch (meshResolution)
            {
                case MeshResolution.Full:
                    resX = srcWidth;
                    resZ = srcHeight;
                    break;
                case MeshResolution.Medium:
                    resX = Mathf.Max(2, srcWidth / 2);
                    resZ = Mathf.Max(2, srcHeight / 2);
                    break;
                case MeshResolution.Low:
                    resX = Mathf.Max(2, srcWidth / 4);
                    resZ = Mathf.Max(2, srcHeight / 4);
                    break;
                case MeshResolution.VeryLow:
                    resX = Mathf.Max(2, srcWidth / 8);
                    resZ = Mathf.Max(2, srcHeight / 8);
                    break;
                default:
                    resX = srcWidth;
                    resZ = srcHeight;
                    break;
            }
        }

        private Mesh GenerateHeightmapMesh(byte[] raw, int srcWidth, int srcHeight, 
                                           int meshResX, int meshResZ,
                                           float sizeX, float sizeY, float sizeZ)
        {
            int vertCount = meshResX * meshResZ;
            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];

            // Generate vertices
            for (int z = 0; z < meshResZ; z++)
            {
                float vNorm = (float)z / (meshResZ - 1);
                float srcZf = vNorm * (srcHeight - 1);
                int srcZ = Mathf.RoundToInt(srcZf);

                for (int x = 0; x < meshResX; x++)
                {
                    float uNorm = (float)x / (meshResX - 1);
                    float srcXf = uNorm * (srcWidth - 1);
                    int srcX = Mathf.RoundToInt(srcXf);

                    int pixelIndex = (srcZ * srcWidth + srcX) * 2;
                    float height = 0f;

                    if (pixelIndex + 1 < raw.Length)
                    {
                        ushort v16 = BitConverter.ToUInt16(raw, pixelIndex);
                        height = (v16 / 65535f) * sizeY;
                    }

                    int vertIndex = z * meshResX + x;
                    vertices[vertIndex] = new Vector3(
                        uNorm * sizeX,
                        height,
                        vNorm * sizeZ
                    );
                    uvs[vertIndex] = new Vector2(uNorm, vNorm);
                }
            }

            // Generate triangles
            int quadCount = (meshResX - 1) * (meshResZ - 1);
            int[] triangles = new int[quadCount * 6];
            int triIndex = 0;

            for (int z = 0; z < meshResZ - 1; z++)
            {
                for (int x = 0; x < meshResX - 1; x++)
                {
                    int bottomLeft = z * meshResX + x;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = (z + 1) * meshResX + x;
                    int topRight = topLeft + 1;

                    // First triangle
                    triangles[triIndex++] = bottomLeft;
                    triangles[triIndex++] = topLeft;
                    triangles[triIndex++] = bottomRight;

                    // Second triangle
                    triangles[triIndex++] = bottomRight;
                    triangles[triIndex++] = topLeft;
                    triangles[triIndex++] = topRight;
                }
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = vertCount > 65535 
                ? UnityEngine.Rendering.IndexFormat.UInt32 
                : UnityEngine.Rendering.IndexFormat.UInt16;
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
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

        // ---------------------------------------------------------------------
        // Batch meshes from selection
        // ---------------------------------------------------------------------
        private void CreateMeshesFromSelection()
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
                Debug.LogWarning("GeoTiffTerrainWindow: No selected textures had metadata. Creating meshes without positional offsets.");
                baseMinX = float.MaxValue;
                baseMinY = float.MaxValue;
            }

            foreach (var tex in textures)
            {
                HeightmapMeta meta = null;
                metaCache.TryGetValue(tex, out meta);

                CreateSingleMesh(tex, meta, baseMinX, baseMinY);
            }

            Debug.Log("GeoTiffTerrainWindow: Created " + textures.Count + " mesh(es).");
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
