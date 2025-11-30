# Unity GeoTiff Importer

Import GEOTIFF files and Create Terrains from it.

Tested with Maanmittauslaitos 2m GeoTiff files: https://asiointi.maanmittauslaitos.fi/karttapaikka/tiedostopalvelu/korkeusmalli

### Features
- Reads metadata from geotiff (pixel size in meters and tile size)

### Usage
- Install this plugin files into your project (TODO add UPM support)
- Rename your .tif files into .geotif or .geotiff (because Unity already imports .tiff files internall)
- Copy your *.geotif files into Unity Project
- Your geotiff files should now appear as imported files

### Other tools
- You can use .jp2 converter to get ortho images as png/jpg/tiff https://github.com/unitycoder/JP2Converter

### Images
<img width="1111" height="879" alt="image" src="https://github.com/user-attachments/assets/b1545588-a4bd-4add-a5ac-50a708df1489" />
<img width="923" height="565" alt="image" src="https://github.com/user-attachments/assets/8c746425-44df-4533-84d1-db0288022601" />
<img width="2560" height="1400" alt="image" src="https://github.com/user-attachments/assets/a43b1eb9-a7e7-49e9-b8e9-4d6e74c16740" />


### External Licenses
- libtiff.net : https://github.com/BitMiracle/libtiff.net?tab=License-1-ov-file#readme
