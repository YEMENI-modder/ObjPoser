# Third-party notices

ObjPoser bundles the following MIT-licensed libraries **unmodified**, as source files in [`src/ThirdParty/`](src/ThirdParty/):

| Library | Used for | Author | Source | License file |
|---|---|---|---|---|
| **TGASharpLib** | reading `.tga` textures | Alex Green (Zelenskyi Alexandr) | https://github.com/ALEXGREENALEX/TGASharpLib | [`LICENSE-TGASharpLib.txt`](src/ThirdParty/LICENSE-TGASharpLib.txt) |
| **DDSReaderSharp** | reading `.dds` textures (DXT1-5 and common uncompressed formats) | Alex Green (Zelenskyi Alexandr) | https://github.com/ALEXGREENALEX/DDSReaderSharp | [`LICENSE-DDSReaderSharp.txt`](src/ThirdParty/LICENSE-DDSReaderSharp.txt) |

DDSReaderSharp is a C# port of `DDSReader.java` by Kenji Sasaki (MIT).

All other image formats (PNG, JPG, BMP, GIF, TIFF) are decoded by `System.Drawing` from the .NET Framework.

The ObjPoser source code itself is licensed under the MIT License, see [`LICENSE`](LICENSE).
