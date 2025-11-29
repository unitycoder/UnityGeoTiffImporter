// https://github.com/unitycoder/UnityGeoTiffImporter

using UnityEngine;

namespace UnityLibrary.Importers
{
    public class HeightmapMeta : ScriptableObject
    {
        // Height info (DEM values, meters)
        public float minHeight;
        public float maxHeight;

        // Raster size (pixels)
        public int rasterWidth;
        public int rasterHeight;

        // Pixel size in map units (typically meters)
        public float pixelSizeX;   // width of one pixel
        public float pixelSizeY;   // height of one pixel

        // Map coordinates (CRS units) of upper-left corner
        public float originX;      // e.g. 638000
        public float originY;      // e.g. 7002000

        // Map bounds (CRS units)
        public float minX;         // west
        public float maxX;         // east
        public float minY;         // south
        public float maxY;         // north

        // CRS
        public int epsgCode;       // e.g. 3067

        // NoData
        public bool hasNoData;
        public float noDataValue;
    }
}