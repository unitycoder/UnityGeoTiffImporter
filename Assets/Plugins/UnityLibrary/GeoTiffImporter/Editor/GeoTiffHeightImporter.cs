// https://github.com/unitycoder/UnityGeoTiffImporter

using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEditor.AssetImporters;
using BitMiracle.LibTiff.Classic;

namespace UnityLibrary.Importers
{
    [ScriptedImporter(1, new[] { "geotif", "geotiff" })]
    public class GeoTiffHeightImporter : ScriptedImporter
    {
        [Tooltip("If true, min/max is taken from the file. If false, fixed range below is used.")]
        public bool autoDetectRange = true;

        [Tooltip("Used if autoDetectRange = false (in meters or whatever your DEM units are).")]
        public float manualMinHeight = 0f;
        public float manualMaxHeight = 500f;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string path = ctx.assetPath;
            using (Tiff tif = Tiff.Open(path, "r"))
            {
                if (tif == null)
                {
                    Debug.LogError("GeoTiffHeightImporter: Could not open " + path);
                    return;
                }

                int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
                int bitsPerSample = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
                int samplesPerPixel = tif.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();

                FieldValue[] sfField = tif.GetField(TiffTag.SAMPLEFORMAT);
                SampleFormat sampleFormat = SampleFormat.UINT;
                if (sfField != null)
                    sampleFormat = (SampleFormat)sfField[0].ToInt();

                bool isTiled = tif.IsTiled();
                int tileWidth = 0;
                int tileHeight = 0;

                if (isTiled)
                {
                    tileWidth = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
                    tileHeight = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
                }

                Debug.Log(
                    "GeoTiffHeightImporter: " + Path.GetFileName(path) +
                    " width=" + width +
                    " height=" + height +
                    " bitsPerSample=" + bitsPerSample +
                    " samplesPerPixel=" + samplesPerPixel +
                    " sampleFormat=" + sampleFormat +
                    " isTiled=" + isTiled +
                    (isTiled ? (" tileWidth=" + tileWidth + " tileHeight=" + tileHeight) : "")
                );

                if (samplesPerPixel != 1)
                {
                    Debug.LogError("GeoTiffHeightImporter: Only single-band GeoTIFFs are supported. Got " +
                                   samplesPerPixel + " bands.");
                    return;
                }

                if (bitsPerSample != 32 && bitsPerSample != 16)
                {
                    Debug.LogError("GeoTiffHeightImporter: Only 16-bit and 32-bit samples are supported. Got " +
                                   bitsPerSample + " bits.");
                    return;
                }

                // Read all samples as float
                float[] elevation = new float[width * height];

                if (isTiled)
                {
                    ReadTiled(tif, width, height, bitsPerSample, tileWidth, tileHeight, elevation);
                }
                else
                {
                    ReadScanlines(tif, width, height, bitsPerSample, elevation);
                }

                // Compute min/max
                float min = manualMinHeight;
                float max = manualMaxHeight;

                if (autoDetectRange)
                {
                    min = float.MaxValue;
                    max = float.MinValue;

                    for (int i = 0; i < elevation.Length; i++)
                    {
                        float v = elevation[i];
                        if (float.IsNaN(v) || float.IsInfinity(v))
                            continue;

                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }

                Debug.Log("GeoTiffHeightImporter: elevation min=" + min + " max=" + max);

                if (elevation.Length > 0)
                {
                    int mid = elevation.Length / 2;
                    Debug.Log(
                        "GeoTiffHeightImporter: first=" + elevation[0] +
                        " mid=" + elevation[mid] +
                        " last=" + elevation[elevation.Length - 1]
                    );
                }

                float range = Mathf.Max(0.0001f, max - min);

                // Convert to 16-bit UNorm for Unity
                byte[] raw16 = new byte[width * height * 2];
                int b = 0;

                for (int i = 0; i < elevation.Length; i++)
                {
                    float v = (elevation[i] - min) / range; // 0..1
                    if (v < 0f) v = 0f;
                    if (v > 1f) v = 1f;

                    ushort h16 = (ushort)(v * 65535f);
                    raw16[b++] = (byte)(h16 & 0xFF);
                    raw16[b++] = (byte)((h16 >> 8) & 0xFF);
                }

                var tex = new Texture2D(width, height, TextureFormat.R16, false, true);
                tex.name = Path.GetFileNameWithoutExtension(path) + "_Height";
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;

                tex.LoadRawTextureData(raw16);
                tex.Apply(false, true);

                ctx.AddObjectToAsset("Heightmap", tex);
                ctx.SetMainObject(tex);

                // ---------------------------------------------------------------------
                // Extra GeoTIFF metadata
                // ---------------------------------------------------------------------

                // 1) Pixel size (ModelPixelScaleTag 33550)
                double pixelSizeX = 1.0;
                double pixelSizeY = 1.0;

                FieldValue[] scaleField = tif.GetField((TiffTag)33550); // ModelPixelScaleTag
                if (scaleField != null && scaleField.Length >= 2)
                {
                    double[] scales = scaleField[1].ToDoubleArray();
                    if (scales != null && scales.Length >= 2)
                    {
                        pixelSizeX = scales[0];
                        pixelSizeY = scales[1];
                    }
                }

                // 2) Origin (ModelTiepointTag 33922)
                double originX = 0.0;
                double originY = 0.0;

                FieldValue[] tieField = tif.GetField((TiffTag)33922); // ModelTiepointTag
                if (tieField != null && tieField.Length >= 2)
                {
                    double[] tiepoints = tieField[1].ToDoubleArray();
                    // tiepoints come in groups of 6: rasterX, rasterY, rasterZ, modelX, modelY, modelZ
                    if (tiepoints != null && tiepoints.Length >= 6)
                    {
                        originX = tiepoints[3];
                        originY = tiepoints[4];
                    }
                }

                // 3) Map bounds (assuming north-up, origin at upper-left)
                double minX = originX;
                double maxX = originX + pixelSizeX * width;
                double maxY = originY;
                double minY = originY - pixelSizeY * height;

                // 4) EPSG code from GeoKeyDirectoryTag (34735)
                int epsgCode = 0;
                FieldValue[] geoKeyDirField = tif.GetField((TiffTag)34735); // GeoKeyDirectoryTag
                if (geoKeyDirField != null && geoKeyDirField.Length >= 2)
                {
                    short[] keyDir = geoKeyDirField[1].ToShortArray();
                    if (keyDir != null && keyDir.Length >= 4)
                    {
                        int keyCount = keyDir[3];
                        int offset = 4;
                        for (int i = 0; i < keyCount; i++)
                        {
                            if (offset + 3 >= keyDir.Length)
                                break;

                            ushort keyId = (ushort)keyDir[offset + 0];
                            ushort tiffTagLocation = (ushort)keyDir[offset + 1];
                            ushort count = (ushort)keyDir[offset + 2];
                            ushort valueOffset = (ushort)keyDir[offset + 3];

                            // ProjectedCSTypeGeoKey
                            if (keyId == 3072)
                            {
                                epsgCode = valueOffset; // EPSG code
                                break;
                            }

                            offset += 4;
                        }
                    }
                }

                // 5) NoData value from GDAL_NODATA tag (42113)
                bool hasNoData = false;
                float noDataValue = 0f;

                FieldValue[] noDataField = tif.GetField((TiffTag)42113);
                if (noDataField != null && noDataField.Length >= 2)
                {
                    string noDataStr = noDataField[1].ToString();
                    if (!string.IsNullOrEmpty(noDataStr) &&
                        float.TryParse(noDataStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float parsed))
                    {
                        hasNoData = true;
                        noDataValue = parsed;
                    }
                }

                // ---------------------------------------------------------------------
                // Store metadata as a sub-asset
                // ---------------------------------------------------------------------

                var meta = ScriptableObject.CreateInstance<HeightmapMeta>();
                meta.minHeight = min;
                meta.maxHeight = max;

                meta.rasterWidth = width;
                meta.rasterHeight = height;

                meta.pixelSizeX = (float)pixelSizeX;
                meta.pixelSizeY = (float)pixelSizeY;

                meta.originX = (float)originX;
                meta.originY = (float)originY;

                meta.minX = (float)minX;
                meta.maxX = (float)maxX;
                meta.minY = (float)minY;
                meta.maxY = (float)maxY;

                meta.epsgCode = epsgCode;

                meta.hasNoData = hasNoData;
                meta.noDataValue = noDataValue;

                ctx.AddObjectToAsset("Meta", meta);
            }
        }

        private static void ReadScanlines(Tiff tif, int width, int height, int bitsPerSample, float[] elevation)
        {
            int scanlineSize = tif.ScanlineSize();
            byte[] scanline = new byte[scanlineSize];

            if (bitsPerSample == 32)
            {
                float[] rowFloats = new float[width];

                for (int y = 0; y < height; y++)
                {
                    tif.ReadScanline(scanline, y);
                    Buffer.BlockCopy(scanline, 0, rowFloats, 0, width * 4);

                    for (int x = 0; x < width; x++)
                    {
                        int idx = (height - 1 - y) * width + x; // flip vertically
                        elevation[idx] = rowFloats[x];
                    }
                }
            }
            else // 16-bit
            {
                for (int y = 0; y < height; y++)
                {
                    tif.ReadScanline(scanline, y);

                    for (int x = 0; x < width; x++)
                    {
                        int byteIndex = x * 2;
                        if (byteIndex + 1 >= scanline.Length)
                            break;

                        ushort u = BitConverter.ToUInt16(scanline, byteIndex);
                        int idx = (height - 1 - y) * width + x;
                        elevation[idx] = u;
                    }
                }
            }
        }

        private static void ReadTiled(Tiff tif, int width, int height, int bitsPerSample,
                                      int tileWidth, int tileHeight, float[] elevation)
        {
            int bytesPerPixel = bitsPerSample / 8;
            int tileCount = tif.NumberOfTiles();
            int tileStride = (width + tileWidth - 1) / tileWidth;

            int bufferSize = tileWidth * tileHeight * bytesPerPixel;
            byte[] tileBuffer = new byte[bufferSize];

            if (bitsPerSample == 32)
            {
                float[] tileFloats = new float[tileWidth * tileHeight];

                for (int t = 0; t < tileCount; t++)
                {
                    // ReadEncodedTile count must be decompressed size (bufferSize), not RawTileSize
                    tif.ReadEncodedTile(t, tileBuffer, 0, bufferSize);
                    Buffer.BlockCopy(tileBuffer, 0, tileFloats, 0, bufferSize);

                    int tileX = tileWidth * (t % tileStride);
                    int tileY = tileHeight * (t / tileStride);

                    int copyWidth = Math.Min(tileWidth, width - tileX);
                    int copyHeight = Math.Min(tileHeight, height - tileY);

                    for (int j = 0; j < copyHeight; j++)
                    {
                        for (int i = 0; i < copyWidth; i++)
                        {
                            float v = tileFloats[j * tileWidth + i];

                            int x = tileX + i;
                            int y = tileY + j;

                            // flip vertically when storing
                            int idx = (height - 1 - y) * width + x;
                            elevation[idx] = v;
                        }
                    }
                }
            }
            else // 16-bit, tiled
            {
                ushort[] tileUshorts = new ushort[tileWidth * tileHeight];

                for (int t = 0; t < tileCount; t++)
                {
                    tif.ReadEncodedTile(t, tileBuffer, 0, bufferSize);
                    Buffer.BlockCopy(tileBuffer, 0, tileUshorts, 0, bufferSize);

                    int tileX = tileWidth * (t % tileStride);
                    int tileY = tileHeight * (t / tileStride);

                    int copyWidth = Math.Min(tileWidth, width - tileX);
                    int copyHeight = Math.Min(tileHeight, height - tileY);

                    for (int j = 0; j < copyHeight; j++)
                    {
                        for (int i = 0; i < copyWidth; i++)
                        {
                            ushort u = tileUshorts[j * tileWidth + i];

                            int x = tileX + i;
                            int y = tileY + j;

                            int idx = (height - 1 - y) * width + x;
                            elevation[idx] = u;
                        }
                    }
                }
            }
        }
    }
}
