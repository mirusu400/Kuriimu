using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Cetera.Image;
using Kuriimu.Compression;
using Kuriimu.Kontract;
using Kuriimu.IO;

namespace image_level5.imgc
{
    public class IMGC
    {
        public static Header header;
        public static byte[] entryStart = null;

        public static Bitmap Load(Stream input)
        {
            using (var br = new BinaryReaderX(input))
            {
                //Header
                header = br.ReadStruct<Header>();
                if (header.imageFormat == Format.ETC1 && header.bitDepth == 8)
                    header.imageFormat = Format.ETC1A4;

                //get tile table
                br.BaseStream.Position = header.tableDataOffset;
                byte[] table = Level5.Decompress(new MemoryStream(br.ReadBytes(header.tableSize1)));

                //get image data
                br.BaseStream.Position = header.tableDataOffset + header.tableSize2;
                byte[] tex = Level5.Decompress(new MemoryStream(br.ReadBytes(header.imgDataSize)));

                //order pic blocks by table
                byte[] pic = Order(new MemoryStream(table), new MemoryStream(tex));

                //return finished image
                var settings = new ImageSettings
                {
                    Width = header.width,
                    Height = header.height,
                    Orientation = Orientation.TransposeTile,
                    Format = ImageSettings.ConvertFormat(header.imageFormat),
                    PadToPowerOf2 = false
                };
                return Common.Load(pic, settings);
            }
        }

        public static byte[] Order(MemoryStream tableStream, MemoryStream texStream)
        {
            using (var table = new BinaryReaderX(tableStream))
            using (var tex = new BinaryReaderX(texStream))
            {
                var bitDepth = Common.GetBitDepth(ImageSettings.ConvertFormat(header.imageFormat));

                int tableLength = (int)table.BaseStream.Length;

                var tmp = table.ReadUInt16();
                table.BaseStream.Position = 0;
                var entryLength = 2;
                if (tmp * (64 * bitDepth / 8) > table.BaseStream.Length)
                {
                    entryStart = table.ReadBytes(8);
                    entryLength = 4;
                }

                var ms = new MemoryStream();
                for (int i = (int)table.BaseStream.Position; i < tableLength; i += entryLength)
                {
                    int entry = (entryLength == 2) ? table.ReadUInt16() : table.ReadInt32();
                    if (entry == 0xFFFF)
                    {
                        for (int j = 0; j < 64 * bitDepth / 8; j++)
                        {
                            ms.WriteByte(0);
                        }
                    }
                    else
                    {
                        if (entry * (64 * bitDepth / 8) < tex.BaseStream.Length)
                        {
                            tex.BaseStream.Position = entry * (64 * bitDepth / 8);
                            for (int j = 0; j < 64 * bitDepth / 8; j++)
                            {
                                ms.WriteByte(tex.ReadByte());
                            }
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        public static void Save(string filename, Bitmap bitmap)
        {
            int width = (bitmap.Width + 0x7) & ~0x7;
            int height = (bitmap.Height + 0x7) & ~0x7;

            var settings = new ImageSettings
            {
                Width = width,
                Height = height,
                Orientation = Orientation.TransposeTile,
                Format = ImageSettings.ConvertFormat(header.imageFormat),
                PadToPowerOf2 = false
            };
            byte[] pic = Common.Save(bitmap, settings);

            using (var bw = new BinaryWriterX(File.Create(filename)))
            {
                //Header
                header.width = (short)bitmap.Width;
                header.height = (short)bitmap.Height;

                //tile table
                var table = new MemoryStream();
                byte[] importPic = Deflate(pic, ImageSettings.ConvertFormat(header.imageFormat), out table);

                header.tableSize1 = (int)table.Length + 4;
                header.tableSize2 = (header.tableSize1 + 3) & ~3;
                header.imgDataSize = importPic.Length + 4;
                bw.WriteStruct(header);

                bw.Write(Level5.Compress(table, Level5.Method.LZ10));

                bw.BaseStream.Position = 0x48 + header.tableSize2;
                bw.Write(Level5.Compress(new MemoryStream(importPic), Level5.Method.LZ10));
            }
        }

        public static byte[] Deflate(byte[] pic, Cetera.Image.Format format, out MemoryStream table)
        {
            table = new MemoryStream();
            using (var tableB = new BinaryWriterX(table, true))
            {
                if (entryStart != null) tableB.Write(entryStart);

                List<byte[]> parts = new List<byte[]>();

                using (var picB = new BinaryReaderX(new MemoryStream(pic)))
                    while (picB.BaseStream.Position < picB.BaseStream.Length)
                    {
                        byte[] part = picB.ReadBytes(64 * Common.GetBitDepth(format) / 8);

                        if (parts.Find(x => x.SequenceEqual(part)) != null)
                            if (entryStart != null) tableB.Write(parts.FindIndex(x => x.SequenceEqual(part))); else tableB.Write((short)parts.FindIndex(x => x.SequenceEqual(part)));
                        else
                        {
                            if (entryStart != null) tableB.Write(parts.Count); else tableB.Write((short)parts.Count);
                            parts.Add(part);
                        }
                    }

                return parts.SelectMany(x => x.SelectMany(b => new[] { b })).ToArray();
            }
        }
    }
}
