// https://github.com/unitycoder/UnityGeoTiffImporter

using UnityEngine;

namespace UnityLibrary.Importers
{
    [CreateAssetMenu(fileName = "HeightmapMeta", menuName = "GeoTIFF/Heightmap Metadata", order = 1)]

    public class HeightmapMeta : ScriptableObject
    {
        public float minHeight;
        public float maxHeight;

        public float pixelSizeX = 1f; // meters per pixel (X)
        public float pixelSizeY = 1f; // meters per pixel (Y)
    }
}