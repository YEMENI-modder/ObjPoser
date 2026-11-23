using System;
using System.Drawing;
using System.IO;
using TGASharpLib;   // https://github.com/ALEXGREENALEX/TGASharpLib  (MIT)

namespace ObjPoser
{
    // Loads textures referenced by the OBJ's .mtl file.
    //   TGA  -> TGASharpLib          DDS -> DDSReaderSharp (DXT1-5 + common uncompressed DDS formats)
    //   PNG / JPG / BMP / GIF / TIFF / ICO -> System.Drawing
    public static class TextureLoader
    {
        public static readonly string[] Extensions = { ".png", ".tga", ".dds", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

        public static Bitmap Load(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isDds = d.Length > 4 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ';
            Exception last = null;
            if (isDds || ext == ".dds")
            {
                try { return DDSReaderSharp.ToBitmap(d); } catch (Exception e) { last = e; }
            }
            if (ext == ".tga")
            {
                try { return new TGA(d).ToBitmap(); } catch (Exception e) { last = e; }
            }
            try
            {
                using (MemoryStream ms = new MemoryStream(d))
                using (Image im = Image.FromStream(ms)) return new Bitmap(im);
            }
            catch (Exception e) { last = e; }
            try { return new TGA(d).ToBitmap(); } catch (Exception e) { last = e; }
            throw new Exception("Unsupported texture: " + Path.GetFileName(path) + (last != null ? " (" + last.Message + ")" : ""));
        }

        // finds the file named in the .mtl, or the same name with another known extension
        public static string Find(string dir, string name)
        {
            name = name.Trim().Trim('"');
            string p = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
            if (File.Exists(p)) return p;
            string baseName = Path.GetFileNameWithoutExtension(name);
            string folder = Path.IsPathRooted(name) ? Path.GetDirectoryName(name) : dir;
            if (folder == null || !Directory.Exists(folder)) return null;
            foreach (string e in Extensions)
                foreach (string f in Directory.GetFiles(folder, baseName + ".*"))
                    if (string.Equals(Path.GetExtension(f), e, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }
    }
}
