# Third-party notices

## IGRF

`config/geomagnetics/igrf.dat` contains International Geomagnetic Reference Field coefficient data, with the original NOAA/NGDC attribution retained in its header. The model is developed by IAGA. These scientific model data are not authored by MagOps and are not relicensed under the MagOps MIT license. Model epochs available in the bundled file govern supported dates; do not assume it is the latest IGRF generation.

Official reference: https://www.ncei.noaa.gov/products/international-geomagnetic-reference-field

## .NET

Self-contained Windows releases include Microsoft .NET runtime components. Their license and notices are distributed in the `licenses` directory of the release ZIP. See https://github.com/dotnet/runtime and https://github.com/dotnet/windowsdesktop.

## Copernicus DEM

The application downloads GLO-30 Public or GLO-90 on request from the public AWS collection. Each local source receipt identifies the product, original object URL, download time, checksum and attribution. The release contains a synthetic regression-test raster in its source tree. Refer to the [Copernicus product and licensing page](https://dataspace.copernicus.eu/explore-data/data-collections/copernicus-contributing-missions/collections-description/COP-DEM) and the [AWS data catalog](https://registry.opendata.aws/copernicus-dem/).

## AW3D30

AW3D30 data are supplied by JAXA and its partners under their own data terms. No terrain tiles or account credentials are included in this repository or release. Downloads retain their source information; users are responsible for required credits and applicable notifications.

Policy: https://earth.jaxa.jp/en/data/policy/

## File formats and trademarks

KML, DJI and Ovi references identify interoperability targets, not affiliation or endorsement. Public KML examples are synthetic. No vendor KMZ template is redistributed.

## Word report components

Word report generation uses the MIT-licensed Open XML SDK, DocxTemplater, Dynamic Expresso and System.IO.Packaging. See the NuGet lockfile for versions and the licenses directory in the binary distribution for the notices.
