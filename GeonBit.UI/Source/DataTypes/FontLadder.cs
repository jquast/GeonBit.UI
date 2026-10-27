using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace GeonBit.UI.DataTypes
{
    public sealed class FontFace
    {
        public readonly int PixelSize;
        public readonly SpriteFont Font;
        public readonly HashSet<char> Characters = new HashSet<char>();

        public FontFace(int pixelSize, SpriteFont font)
        {
            PixelSize = pixelSize;
            Font = font;
            foreach (char c in font.Characters)
            {
                Characters.Add(c);
            }
        }
    }

    public sealed class FontLadder
    {
        public readonly FontFace[] Faces;

        public bool Nearest;

        public bool PreferNative;

        public FontLadder(IEnumerable<FontFace> faces)
        {
            var list = new List<FontFace>(faces);
            list.Sort((a, b) => a.PixelSize.CompareTo(b.PixelSize));
            Faces = list.ToArray();
        }

        public FontFace Pick(float targetPixelSize)
        {
            if (Faces.Length == 0)
            {
                return null;
            }
            if (Nearest)
            {
                var nearest = Faces[0];
                foreach (var face in Faces)
                {
                    if (Math.Abs(face.PixelSize - targetPixelSize) < Math.Abs(nearest.PixelSize - targetPixelSize))
                    {
                        nearest = face;
                    }
                }
                return nearest;
            }
            foreach (var face in Faces)
            {
                if (face.PixelSize >= targetPixelSize)
                {
                    return face;
                }
            }
            return Faces[Faces.Length - 1];
        }

        public FontFace PickNative(float targetPixelSize)
        {
            if (Faces.Length == 0)
            {
                return null;
            }
            var best = Faces[0];
            foreach (var face in Faces)
            {
                if (face.PixelSize <= targetPixelSize)
                {
                    best = face;
                }
                else
                {
                    break;
                }
            }
            return best;
        }
    }
}
